; ============================================================================
;  RevitRebarModeler 설치 스크립트 (Inno Setup 6)
;  대상 Revit 버전: 2024
;  설치 위치: C:\ProgramData\Autodesk\Revit\Addins\2024\
;    - RevitRebarModeler.addin               (manifest)
;    - RevitRebarModeler\*.dll               (assemblies + dependencies)
; ============================================================================

#define MyAppName       "RevitRebarModeler"
#define MyAppVersion    "1.0.7"
#define MyAppPublisher  "Geotechnical Tunnel Division R&D"
#define MyAppDescription "Revit 보강철근 모델링 / 수량 일람표 애드인 (Revit 2024)"
#define RevitVersion    "2024"

[Setup]
AppId={{E01C4E10-70A1-4DA1-93A5-5454B8276302}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments={#MyAppDescription}
DefaultDirName={commonappdata}\Autodesk\Revit\Addins\{#RevitVersion}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=Output
OutputBaseFilename={#MyAppName}_Setup_v{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
WizardStyle=modern
UninstallDisplayName={#MyAppName} (Revit {#RevitVersion})
UninstallDisplayIcon={app}\{#MyAppName}\{#MyAppName}.dll

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

; 이전 버전 잔존 파일 제거 — 신규 빌드에서 제거된 dll이 남는 문제 방지
[InstallDelete]
Type: filesandordirs; Name: "{app}\{#MyAppName}"

[Files]
; manifest (addin) — Addins\2024\ 루트에 직접 배치
Source: "dist\{#MyAppName}.addin"; DestDir: "{app}"; Flags: ignoreversion

; DLL + 의존성 — Addins\2024\RevitRebarModeler\ 하위 폴더에 배치
Source: "dist\{#MyAppName}\*"; DestDir: "{app}\{#MyAppName}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Code]
function InitializeSetup(): Boolean;
var
  RevitExe: String;
begin
  RevitExe := ExpandConstant('{commonpf}\Autodesk\Revit {#RevitVersion}\Revit.exe');
  if not FileExists(RevitExe) then
  begin
    if MsgBox('Revit {#RevitVersion}이(가) 설치되어 있지 않은 것 같습니다.' + #13#10 +
              '({#MyAppName}은 Revit {#RevitVersion} 전용 애드인입니다)' + #13#10#13#10 +
              '그래도 설치를 계속하시겠습니까?',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  end;
  Result := True;
end;
