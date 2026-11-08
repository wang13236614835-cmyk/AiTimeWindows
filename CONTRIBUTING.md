# Contributing

AiTime Windows is a Windows desktop focus and study-planning application.

1. Make changes in `src/` and use `Build.ps1` to compile with the .NET Framework compiler.
2. Run `bin/AiTime.exe --self-test validation/self-test.json` before submitting changes.
3. Do not include `%LOCALAPPDATA%/AiTimeWindows`, private learning data, credentials, workstation backups, or binaries in commits.
4. Report potential security issues privately to maintainers rather than publishing exploit details or secrets in issues.
5. The app controls ordinary user-session windows. It does not attempt to bypass operating-system security controls.
