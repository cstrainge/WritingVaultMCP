# Access integration-test template (historical)

This procedure is retired. Current tests create a fresh disposable Access database through `AccessDatabaseFileInitializer`; the old template remains local and ignored by Git.

`WritingVault.Integration.Template.accdb` is a verified empty Phase 0 copy used only as the source for disposable integration databases.

Tests must:

1. Copy the template to a unique temporary path.
2. Clear the copied file's read-only attribute if necessary.
3. Run migrations and mutations only against that copy.
4. Close every ACE connection.
5. Confirm the temporary `.laccdb` lock file is gone.
6. Delete the temporary database after the test run.

Tests must never mutate this template or the user's production `WritingVault.accdb`.
