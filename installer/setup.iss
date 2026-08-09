; Shabana Inventory — PC Edition installer.
;
; Compiled by the GitHub Actions windows-latest runner (real Windows, not
; Wine) — see .github/workflows/build-installer.yml, which stages every
; binary/script/config/migration this script packages into ./payload
; before calling ISCC.exe on this file.
;
; Deliberately does NOT ask the user for an install path, database
; credentials, or an admin account. See BUILD_PLAN.md "First-run flow":
; the installer does only mechanical setup; the app itself detects
; "no admin yet" and shows its own setup screen. A wizard page that has to
; initdb + apply 32 migrations + start services has no good story for
; "retry" if step two fails midway — better to fail fast to a log file.

#define MyAppName "مخزون شبانة"
#define MyAppVersion GetEnv("SHABANA_VERSION")
#if MyAppVersion == ""
  #define MyAppVersion "0.0.0-dev"
#endif
#define MyAppPublisher "Shabana"
#define MyInstallDir "C:\ProgramData\Shabana"

[Setup]
AppId={{B6E9B6C1-6B7B-4C9F-9C1A-5D3A6A2E9F11}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={#MyInstallDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=no
OutputBaseFilename=Shabana-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
WizardStyle=modern
; Signed separately post-build (BUILD_PLAN.md item #5) — deferred until
; the installer works end-to-end unsigned.
; Custom icon (SetupIconFile / UninstallDisplayIcon) deferred alongside
; signing — a missing SetupIconFile fails compilation, so until a real
; icon.ico is added to the payload we use Inno's default. TODO before
; customer release: add branding icon.
UninstallDisplayIcon={uninstallexe}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "payload\bin\*"; DestDir: "{app}\bin"; Flags: recursesubdirs ignoreversion
Source: "payload\www\*"; DestDir: "{app}\www"; Flags: recursesubdirs ignoreversion
Source: "payload\supabase\*"; DestDir: "{app}\supabase"; Flags: recursesubdirs ignoreversion
Source: "payload\installer\*"; DestDir: "{app}\installer"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "http://localhost:8000"
Name: "{autoprograms}\{#MyAppName}\استعادة نسخة احتياطية"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\scripts\restore.ps1"" -InstallDir ""{app}"""
Name: "{autoprograms}\{#MyAppName}\إعادة تعيين كلمة مرور المدير"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\scripts\reset-admin.ps1"" -InstallDir ""{app}"""
Name: "{autoprograms}\{#MyAppName}\تصدير تقرير المشكلة"; Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\scripts\export-report.ps1"" -InstallDir ""{app}"""

[Run]
; Provisioning runs in a visible window (not silently) — if initdb or a
; migration fails, the shop owner needs to see *something* on screen
; rather than a shortcut that silently opens a broken page. Full log is
; also written under {app}\logs.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\scripts\provision.ps1"" -InstallDir ""{app}"""; StatusMsg: "جارٍ إعداد قاعدة البيانات..."; Flags: waituntilterminated

Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\scripts\backup.ps1"" -InstallDir ""{app}"" -Register"; StatusMsg: "جارٍ جدولة النسخ الاحتياطي..."; Flags: waituntilterminated

Filename: "http://localhost:8000"; Description: "فتح البرنامج الآن"; Flags: postinstall shellexec skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Stop-Service ShabanaCaddy,ShabanaGoTrue,ShabanaPostgREST,ShabanaPostgres -ErrorAction SilentlyContinue; & '{app}\bin\nssm\nssm.exe' remove ShabanaCaddy confirm; & '{app}\bin\nssm\nssm.exe' remove ShabanaGoTrue confirm; & '{app}\bin\nssm\nssm.exe' remove ShabanaPostgREST confirm; & '{app}\bin\pg\bin\pg_ctl.exe' unregister -N ShabanaPostgres; Unregister-ScheduledTask -TaskName ShabanaNightlyBackup -Confirm:$false -ErrorAction SilentlyContinue"""; Flags: runhidden

[UninstallDelete]
; Data (data\, backups\, config\) is intentionally NOT deleted by default —
; uninstalling should not be a way to accidentally destroy a shop's sales
; history. The uninstaller only removes services + program files.
Type: filesandordirs; Name: "{app}\bin"
Type: filesandordirs; Name: "{app}\www"
Type: filesandordirs; Name: "{app}\logs"
