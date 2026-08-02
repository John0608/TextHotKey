; TextHotKey per-user 설치 스크립트 (Inno Setup 6.3+)
;
; 신규 사용자용 설치 마법사(setup.exe)를 만든다.
;  - 사용자 폴더(%LOCALAPPDATA%\Programs\TextHotKey)에 per-user 설치
;    → 관리자 권한 없이 설치되고, zip 업데이터도 관리자 권한 없이 파일 교체 가능
;  - 시작 메뉴/(선택)바탕화면 바로가기 + 제거 프로그램 포함
;
; 빌드는 build-release.ps1 / .github/workflows/release.yml에서 아래 define과 함께 호출한다:
;   ISCC /DMyAppVersion=x.y.z /DAppDir=<publish\app> /DOutputDir=<publish> /DIconFile=<favicon.ico>

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef AppDir
  #define AppDir "..\publish\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\publish"
#endif
#ifndef IconFile
  #define IconFile "..\TextHotKey\favicon.ico"
#endif

#define MyAppName "TextHotKey"
#define MyAppExe "TextHotKey.exe"
#define MyAppPublisher "John0608"
#define MyAppURL "https://github.com/John0608/TextHotKey"

[Setup]
; AppId는 업그레이드/제거를 위해 절대 바꾸지 말 것.
AppId={{8F3B2E1A-9C4D-4E7A-B1F6-2A5C7D9E0F31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}

; 관리자 권한 없이 사용자 폴더에 설치한다.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DisableDirPage=auto

UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}

OutputDir={#OutputDir}
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
SetupIconFile={#IconFile}

Compression=lzma2
SolidCompression=yes
WizardStyle=modern

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 설치/업데이트 중 실행 중인 앱을 Restart Manager로 닫아 파일 교체가 가능하게 한다.
CloseApplications=yes
RestartApplications=no
AppMutex=TextHotKey_SingleInstance_Mutex

[Languages]
; 한국어 마법사. Korean.isl은 Inno Setup 6.5+에 기본 번들로 포함된다.
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#AppDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Registry]
; 설치 시엔 건드리지 않고(dontcreatekey), 제거 시 자동시작 항목과 앱 설정 키를 정리한다.
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#MyAppName}"; Flags: dontcreatekey uninsdeletevalue
Root: HKCU; Subkey: "SOFTWARE\{#MyAppName}"; ValueType: none; Flags: dontcreatekey uninsdeletekey
