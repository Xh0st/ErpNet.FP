; Installer Windows (Inno Setup 6) pentru ErpNet.FP – ediția România.
; Se compilează în GitHub Actions (release.yml):
;   ISCC.exe /DMyAppVersion=1.1.0-ro.1 /DPublishDir=..\out\win-x64 installer\ErpNet.FP-RO.iss
;
; Ce face:
;  - copiază serverul în C:\Program Files\ErpNet.FP
;  - instalează și pornește serviciul Windows "ErpNetFPServer" (pornire automată, repornire la eroare)
;  - la actualizare oprește serviciul, înlocuiește fișierele, îl repornește;
;    appsettings.json, jurnalul USN și logurile NU sunt atinse
;  - la dezinstalare oprește și șterge serviciul; appsettings.json, usn-journal și logurile rămân pe disc

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef PublishDir
  #define PublishDir "..\out\win-x64"
#endif

#define MyAppName "ErpNet.FP România"
#define MyServiceName "ErpNetFPServer"
#define MyServiceDisplayName "ErpNet.FP - Fiscal Print Server (RO)"
#define MyExe "ErpNet.FP.Server.exe"

[Setup]
AppId={{6F3B2C1E-8A4D-4E2B-9C7A-0E5D1F2A3B4C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Xh0st / ErpNet and Contributors
AppPublisherURL=https://github.com/Xh0st/ErpNet.FP
AppSupportURL=https://github.com/Xh0st/ErpNet.FP/issues
DefaultDirName={autopf}\ErpNet.FP
DisableProgramDirPage=no
DefaultGroupName=ErpNet.FP
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE.txt
OutputDir=..\dist
OutputBaseFilename=ErpNet.FP-RO-{#MyAppVersion}-setup
SetupIconFile=..\ErpNet.FP.Server\ErpNet.FP.ico
UninstallDisplayIcon={app}\{#MyExe}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "default"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "firewall"; Description: "Permite accesul din rețea la serviciu (port 8001, Windows Firewall)"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "appsettings.json,*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README-RO.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\tools\test-ro.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion

[Icons]
Name: "{group}\ErpNet.FP - pagina de administrare"; Filename: "http://localhost:8001/"
Name: "{group}\ErpNet.FP - folder configurare"; Filename: "{app}"
Name: "{group}\ErpNet.FP - README"; Filename: "{app}\README-RO.md"
Name: "{group}\Dezinstalare ErpNet.FP"; Filename: "{uninstallexe}"

[Run]
; Serviciul Windows
Filename: "{sys}\sc.exe"; Parameters: "create {#MyServiceName} binPath= ""\""{app}\{#MyExe}\"""" start= auto DisplayName= ""{#MyServiceDisplayName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Se instalează serviciul Windows..."; Check: not ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "config {#MyServiceName} binPath= ""\""{app}\{#MyExe}\"""" start= auto DisplayName= ""{#MyServiceDisplayName}"""; Flags: runhidden waituntilterminated; Check: ServiceExists
Filename: "{sys}\sc.exe"; Parameters: "description {#MyServiceName} ""Server HTTP pentru tipărire la casele de marcat prin API JSON (ediția România)."""; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "failure {#MyServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000"; Flags: runhidden waituntilterminated
Filename: "{sys}\sc.exe"; Parameters: "start {#MyServiceName}"; Flags: runhidden waituntilterminated; StatusMsg: "Se pornește serviciul..."
; Firewall (opțional)
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ErpNet.FP"""; Flags: runhidden waituntilterminated; Tasks: firewall
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""ErpNet.FP"" dir=in action=allow protocol=TCP localport=8001"; Flags: runhidden waituntilterminated; Tasks: firewall
; La final
Filename: "http://localhost:8001/"; Description: "Deschide pagina de administrare"; Flags: postinstall shellexec nowait skipifsilent

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#MyServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""ErpNet.FP"""; Flags: runhidden waituntilterminated; RunOnceId: "DeleteFirewallRule"

[Code]
function ServiceExists(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
end;

// La actualizare serviciul ține fișierele deschise: îl oprim înainte de copiere.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if ServiceExists() then
  begin
    Exec(ExpandConstant('{sys}\net.exe'), 'stop {#MyServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2000);
  end;
end;
