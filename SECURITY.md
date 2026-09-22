# Security

Report path traversal, package validation, installer rollback, or unsafe file-operation issues privately to the repository maintainer before public disclosure.

The installer accepts only a package with the expected Local Map identifier, normalizes every relative path, rejects paths that escape the target directory, verifies every SHA-256 checksum, and stages installation before replacing the active plug-in directory.
