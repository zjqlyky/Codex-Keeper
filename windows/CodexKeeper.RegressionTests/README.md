# Windows regression checks

Run the console harness from the repository root:

```powershell
dotnet run --project windows/CodexKeeper.RegressionTests --configuration Release
```

The seven cases create and remove isolated temporary fixtures. They cover normal
string session metadata, saved project grouping, account ownership, malformed
rollouts, Windows path boundaries, persisted consent, and nested project consent.
They do not launch Codex or submit model work.

For a read-only diagnostic of an explicitly supplied real Codex home:

```powershell
dotnet run --project windows/CodexKeeper.RegressionTests --configuration Release -- --inspect-live "$env:USERPROFILE\.codex"
```

This prints availability and counts only. It uses temporary Keeper settings and
does not print task titles, project paths, account IDs, or conversation content.
