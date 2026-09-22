using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeaOfStarsLocalMap;

/// <summary>
/// Temporarily keeps the existing location title and party status panels visible.
/// Acquire before pausing or disabling HUD opening; install HUD Harmony guards in the plugin.
/// </summary>
internal sealed class NativeMapHud : IDisposable
{
    private sealed class PanelState
    {
        internal InGameHudPanel Panel;
        internal Animator Animator;
        internal AnimatorUpdateMode AnimatorMode;
        internal bool WasOpened;
        internal bool OwnsOpenLock;
        internal bool ShowAttempted;
        internal int ModeRepairs;
    }

    private PanelState statuses, zone;
    public InGameHud Hud { get; private set; }
    public TMP_FontAsset NativeFont { get; private set; }
    public bool IsAcquired { get; private set; }

    /// <summary>Returns false without taking ownership when native gameplay/UI guards reject opening.</summary>
    public bool TryAcquire()
    {
        if (IsAcquired) return true;
        try
        {
            var ui = UIManager.Instance;
            var candidate = ui != null ? ui.GetView<InGameHud>() : null;
            if (candidate == null || !candidate.gameObject.activeInHierarchy || !candidate.CanOpenAnyPanel()) return false;

            // LockOpened also clears lockClosed in the native implementation. Never override
            // an existing close lock: refuse acquisition before touching either panel.
            var nextStatuses = Capture(candidate.playerStatuses);
            var nextZone = Capture(candidate.zoneNamePanel);
            if (nextStatuses == null || nextZone == null) return false;
            Hud = candidate; statuses = nextStatuses; zone = nextZone;
            if (!TakeOpenLock(statuses) || !TakeOpenLock(zone)) { Dispose(); return false; }

            statuses.ShowAttempted = true;
            candidate.ShowHP(AnimatorUpdateMode.UnscaledTime);
            zone.ShowAttempted = true;
            candidate.ShowZoneName(AnimatorUpdateMode.UnscaledTime);
            // Match native InGameHud.ProcessInputs: evaluate the newly selected IN
            // animation immediately before game time stops. A stale Hidden event can
            // otherwise deactivate the panel and clear its native open-lock counter.
            statuses.Animator.Update(0f);
            zone.Animator.Update(0f);
            // Pausing immediately after Show can leave the game UI in its first IN
            // frame. Evaluate each controller's authored fully open pose before
            // the caller stops game time; do not rewrite native masks or geometry.
            CompleteIntro(statuses, "HP");
            CompleteIntro(zone, "Zone");
            if (!statuses.Panel.Opened || !zone.Panel.Opened) { Dispose(); return false; }
            IsAcquired = true;

            // The game owns this asset. Keep a reference for native-looking TMP map text;
            // never destroy, change its atlas, or replace the game's own text font.
            try
            {
                var localizer = candidate.zoneNamePanel.smallTitleText;
                var text = localizer != null ? localizer.TextMeshProText : null;
                NativeFont = text != null ? text.font : null;
            }
            catch (Exception error) { Warn("font lookup", error); }
            if (!KeepAlive()) { Dispose(); return false; }
            return true;
        }
        catch (Exception error)
        {
            Warn("acquire", error);
            Dispose();
            return false;
        }
    }

    /// <summary>
    /// Keep panel animations alive while Time.timeScale is zero. False means the native HUD
    /// disappeared or acquired a conflicting close lock; the overlay should close its map.
    /// </summary>
    public bool KeepAlive()
    {
        if (!IsAcquired) return false;
        try
        {
            if (Hud == null || !Hud.gameObject.activeInHierarchy || !CanKeep(statuses) || !CanKeep(zone)) return false;
            EnsureUnscaled(statuses);
            EnsureUnscaled(zone);
            return true;
        }
        catch (Exception error) { Warn("keep alive", error); return false; }
    }

    private static void CompleteIntro(PanelState state, string label)
    {
        var animator = state.Animator;
        int idle = Animator.StringToHash("Base Layer.Idle");
        if (!animator.HasState(0, idle))
            throw new InvalidOperationException(label + " native HUD controller has no Base Layer.Idle state.");
        EnsureUnscaled(state);
        var before = animator.GetCurrentAnimatorStateInfo(0);
        animator.Play(idle, 0, 0f);
        animator.Update(0f);
        var after = animator.GetCurrentAnimatorStateInfo(0);
        Dump("Native map HUD " + label + " intro completed: " + before.shortNameHash +
            "@" + before.normalizedTime + " -> " + after.shortNameHash + "@" +
            after.normalizedTime + "; idle=" + (after.fullPathHash == idle) +
            "; timeScale=" + Time.timeScale + ".");
        if (after.fullPathHash != idle)
            throw new InvalidOperationException(label + " native HUD did not evaluate its open Idle pose.");
    }

    private static void EnsureUnscaled(PanelState state)
    {
        // Assign only when needed. Repeated setters in Update and LateUpdate are
        // unnecessary and obscure whether native code actually changed the mode.
        if (state.Animator.updateMode == AnimatorUpdateMode.UnscaledTime) return;
        state.Animator.updateMode = AnimatorUpdateMode.UnscaledTime;
        state.ModeRepairs++;
    }

    /// <summary>Read-only snapshot for an explicitly requested opening/paused-frame diagnosis.</summary>
    public void DumpState(string label)
    {
        string prefix = "Native map HUD [" + label + "] ";
        try
        {
            if (Hud == null) { Dump(prefix + "HUD=null; acquired=" + IsAcquired); return; }
            var transform = Hud.transform;
            Dump(prefix + "acquired=" + IsAcquired + "; frame=" + Time.frameCount +
                "; timeScale=" + Time.timeScale + "; active=" + Hud.gameObject.activeInHierarchy +
                "; localScale=" + transform.localScale + "; lossyScale=" + transform.lossyScale +
                "; worldPosition=" + transform.position);
            DumpAncestors(prefix + "HUD", transform);
            DumpPanel(prefix + "HP", statuses);
            DumpPanel(prefix + "Zone", zone);
            if (zone?.Panel != null)
            {
                var masks = zone.Panel.GetComponentsInChildren<RectMask2D>(true);
                for (int i = 0; i < masks.Length && i < 8; i++)
                {
                    var mask = masks[i];
                    Dump(prefix + "Zone mask " + mask.name + ": active=" + mask.gameObject.activeInHierarchy +
                        "; enabled=" + mask.enabled + "; padding=" + mask.padding +
                        "; softness=" + mask.softness + "; rect=" + mask.rectTransform.rect);
                }
            }
            if (statuses?.Panel != null)
            {
                DumpRect(prefix + "HP root", statuses.Panel.transform);
                var hpRoot = statuses.Panel.transform;
                for (int i = 0; i < hpRoot.childCount && i < 4; i++)
                {
                    var child = hpRoot.GetChild(i);
                    DumpRect(prefix + "HP child " + child.name, child);
                    for (int j = 0; j < child.childCount && j < 4; j++)
                    {
                        var grandchild = child.GetChild(j);
                        DumpRect(prefix + "HP child " + child.name + "/" + grandchild.name, grandchild);
                    }
                }
            }
        }
        catch (Exception error) { Warn("diagnostic " + label, error); }
    }

    private static void DumpPanel(string label, PanelState state)
    {
        if (state?.Panel == null) { Dump(label + ": panel=null"); return; }
        try
        {
            var panel = state.Panel;
            Dump(label + ": opened=" + panel.Opened + "; activeSelf=" + panel.gameObject.activeSelf +
                "; activeHierarchy=" + panel.gameObject.activeInHierarchy +
                "; lockOpen=" + panel.IsLockedOpen() + "; lockClose=" + panel.IsLockedClose());
            var animator = state.Animator;
            if (animator != null)
            {
                var current = animator.GetCurrentAnimatorStateInfo(0);
                Dump(label + " animator: enabled=" + animator.enabled + "; speed=" + animator.speed +
                    "; mode=" + animator.updateMode + "; culling=" + animator.cullingMode +
                    "; modeRepairs=" + state.ModeRepairs +
                    "; initialized=" + animator.isInitialized + "; controller=" +
                    (animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "null") +
                    "; state=" + current.shortNameHash + "; time=" + current.normalizedTime +
                    "; length=" + current.length + "; stateSpeed=" + current.speed +
                    "; transition=" + animator.IsInTransition(0));
            }
            else Dump(label + " animator=null");
            DumpAncestors(label, panel.transform);
        }
        catch (Exception error) { Warn("diagnostic panel " + label, error); }
    }

    private static void DumpAncestors(string label, Transform transform)
    {
        for (int depth = 0; transform != null && depth < 12; depth++, transform = transform.parent)
        {
            var group = transform.GetComponent<CanvasGroup>();
            if (group != null)
                Dump(label + " group " + transform.name + ": active=" + transform.gameObject.activeInHierarchy +
                    "; enabled=" + group.enabled + "; alpha=" + group.alpha +
                    "; ignoreParents=" + group.ignoreParentGroups);
            var canvas = transform.GetComponent<Canvas>();
            if (canvas != null)
                Dump(label + " canvas " + transform.name + ": enabled=" + canvas.enabled +
                    "; mode=" + canvas.renderMode + "; order=" + canvas.sortingOrder +
                    "; overrideSorting=" + canvas.overrideSorting + "; scale=" + canvas.scaleFactor +
                    "; camera=" + (canvas.worldCamera != null ? canvas.worldCamera.name : "null"));
        }
    }

    private static void DumpRect(string label, Transform transform)
    {
        var rect = transform.GetComponent<RectTransform>();
        if (rect == null) return;
        Dump(label + ": active=" + transform.gameObject.activeInHierarchy + "; local=" + rect.localPosition +
            "; anchored=" + rect.anchoredPosition + "; size=" + rect.sizeDelta +
            "; rect=" + rect.rect + "; scale=" + rect.lossyScale);
    }

    private static void Dump(string message) => StaticMapPlugin.Logger?.LogInfo(message);

    private static PanelState Capture(InGameHudPanel panel)
    {
        if (panel == null || panel.IsLockedClose() || panel.animator == null) return null;
        return new PanelState
        {
            Panel = panel,
            Animator = panel.animator,
            AnimatorMode = panel.animator.updateMode,
            WasOpened = panel.Opened
        };
    }

    private static bool TakeOpenLock(PanelState state)
    {
        if (state.Panel == null || state.Panel.IsLockedClose()) return false;
        state.Panel.LockOpened();
        state.OwnsOpenLock = true;
        return true;
    }

    private static bool CanKeep(PanelState state) => state != null && state.Panel != null &&
        state.Panel.gameObject.activeInHierarchy && state.Animator != null &&
        !state.Panel.IsLockedClose() && state.Panel.Opened;

    /// <summary>Idempotent cleanup. Only balances locks acquired by this helper.</summary>
    public void Dispose()
    {
        var oldStatuses = statuses;
        var oldZone = zone;
        IsAcquired = false; statuses = null; zone = null; Hud = null; NativeFont = null;
        Restore(oldZone);
        Restore(oldStatuses);
    }

    private static void Restore(PanelState state)
    {
        if (state == null) return;
        bool released = !state.OwnsOpenLock;
        try
        {
            if (state.OwnsOpenLock && state.Panel != null)
            {
                state.Panel.UnlockOpened();
                released = true;
            }
        }
        catch (Exception error) { Warn("release panel lock", error); }
        finally { state.OwnsOpenLock = false; }

        try
        {
            // Avoid fighting a new native close lock or another owner's open lock.
            // Usually an originally open panel is still open and needs no operation.
            if (released && state.ShowAttempted && state.Panel != null)
            {
                if (!state.WasOpened && !state.Panel.IsLockedOpen()) state.Panel.HideInstant();
                else if (state.WasOpened && !state.Panel.Opened && !state.Panel.IsLockedClose()) state.Panel.Show();
            }
        }
        catch (Exception error) { Warn("restore panel visibility", error); }
        finally
        {
            try { if (state.Animator != null) state.Animator.updateMode = state.AnimatorMode; }
            catch (Exception error) { Warn("restore animator mode", error); }
        }
    }

    private static void Warn(string stage, Exception error) =>
        StaticMapPlugin.Logger?.LogWarning("Native map HUD " + stage + ": " + error.Message);
}
