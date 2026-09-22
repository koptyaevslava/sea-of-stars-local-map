# Contributing

Keep changes focused on Local Map and preserve the existing Sea of Stars interface. Do not add unrelated mods, extracted game binaries, generated map images, or local machine paths to the repository.

Use English for documentation, comments, identifiers, logs, commit messages, and issue or pull-request descriptions. Runtime localization strings may be represented with Unicode escape sequences when a language requires non-Latin characters.

Before opening a pull request:

1. Run the managed test suite.
2. Build the plug-in against a local BepInEx installation.
3. Run `python scripts/check_english_sources.py`.
4. Verify that no generated payload, game DLL, map image, or build output is staged.
5. Describe any manual in-game checks performed.
