; Installer for the Discagem desktop client on Windows 10/11 (x64).
;
; Build it with installer/windows/build.sh, which publishes the client and runs makensis with:
;   VERSION          display version, for example 0.2.0 or 0.2.0-beta.1
;   VERSION_NUMERIC  four-part file version, for example 0.2.0.0
;   SOURCE_DIR       folder holding the published client (dotnet publish -r win-x64 --self-contained)
;   OUTPUT_FILE      path of the installer to write
;
; The client is installed per user in %LOCALAPPDATA%\Programs\Discagem, so no administrator rights
; are needed. Settings and the saved session live in %APPDATA%\Discagem and survive upgrades.

!ifdef VERSION & VERSION_NUMERIC & SOURCE_DIR & OUTPUT_FILE
!else
  !error "Define VERSION, VERSION_NUMERIC, SOURCE_DIR and OUTPUT_FILE (see installer/windows/build.sh)."
!endif
!ifndef PROJECT_URL
  !define PROJECT_URL "https://github.com/rodriguesarrais/discagem"
!endif

!define APP_NAME "Discagem"
!define APP_EXE "Discagem.exe"
!define UNINSTALLER "Uninstall.exe"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\Discagem"
!define VC_REDIST_URL "https://aka.ms/vs/17/release/vc_redist.x64.exe"

; A 64-bit installer, like the client: it sees the real System32 and registry without redirection.
Target amd64-unicode
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

Name "${APP_NAME}"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
RequestExecutionLevel user
ShowInstDetails nevershow
ShowUninstDetails nevershow
BrandingText "${APP_NAME} ${VERSION}"

VIProductVersion "${VERSION_NUMERIC}"
VIFileVersion "${VERSION_NUMERIC}"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey "CompanyName" "${APP_NAME}"
VIAddVersionKey "LegalCopyright" "${APP_NAME} contributors"

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

; ---------------------------------------------------------------------------------------------------
; Pages (makensis runs from this folder, so the paths below are relative to it)

!define MUI_ICON "..\..\src\Discagem.Client\Assets\discagem.ico"
!define MUI_UNICON "..\..\src\Discagem.Client\Assets\discagem.ico"
!define MUI_WELCOMEFINISHPAGE_BITMAP "assets\wizard.bmp"
!define MUI_UNWELCOMEFINISHPAGE_BITMAP "assets\wizard.bmp"
!define MUI_HEADERIMAGE
!define MUI_HEADERIMAGE_RIGHT
!define MUI_HEADERIMAGE_BITMAP "assets\header.bmp"
!define MUI_HEADERIMAGE_UNBITMAP "assets\header.bmp"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_NODESC

!define MUI_WELCOMEPAGE_TEXT "This will install ${APP_NAME} ${VERSION}, a desktop client for self-hosted text chat, voice channels and screen sharing.$\r$\n$\r$\nIf ${APP_NAME} is already installed, it will be upgraded; your settings and saved session are kept.$\r$\n$\r$\nClose ${APP_NAME} before continuing, then click Next."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Start ${APP_NAME}"
!define MUI_FINISHPAGE_LINK "Project page and release notes"
!define MUI_FINISHPAGE_LINK_LOCATION "${PROJECT_URL}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_COMPONENTS
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

; ---------------------------------------------------------------------------------------------------
; Installer

Function .onInit
  ${IfNot} ${IsNativeAMD64}
  ${AndIfNot} ${IsNativeARM64}
    MessageBox MB_OK|MB_ICONSTOP "${APP_NAME} requires a 64-bit version of Windows." /SD IDOK
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 10240
    MessageBox MB_OK|MB_ICONSTOP "${APP_NAME} requires Windows 10 or later." /SD IDOK
    Abort
  ${EndIf}

  ; Upgrades and the uninstaller delete this folder recursively, so it is fixed and /D is ignored.
  StrCpy $INSTDIR "$LOCALAPPDATA\Programs\${APP_NAME}"
FunctionEnd

; Waits until the client is closed: Windows does not let a running executable be deleted. A client
; that just started this installer to update itself is given a few seconds to exit before the user
; is asked (or, in a silent install, before giving up).
!macro WaitForClientToClose
  StrCpy $R9 0
  ${DoWhile} ${FileExists} "$INSTDIR\${APP_EXE}"
    ClearErrors
    Delete "$INSTDIR\${APP_EXE}"
    ${If} ${Errors}
      ${If} $R9 < 30
        IntOp $R9 $R9 + 1
        Sleep 500
      ${ElseIf} ${Cmd} `MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "${APP_NAME} is running. Quit it (right-click its icon in the notification area and choose Quit Discagem), then click Retry." /SD IDCANCEL IDCANCEL`
        Abort "${APP_NAME} is still running."
      ${EndIf}
    ${EndIf}
  ${Loop}
!macroend

Section "${APP_NAME}" SecApp
  SectionIn RO
  !insertmacro WaitForClientToClose

  ; Start from an empty folder so files of the previous version do not linger.
  RMDir /r "$INSTDIR"
  SetOutPath "$INSTDIR"
  File /r "${SOURCE_DIR}\*.*"
  WriteUninstaller "$INSTDIR\${UNINSTALLER}"

  CreateShortcut "$SMPROGRAMS\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "Publisher" "${APP_NAME}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "URLInfoAbout" "${PROJECT_URL}"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\${UNINSTALLER}"'
  WriteRegStr HKCU "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINSTALLER}" /S'
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "NoRepair" 1
  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  WriteRegDWORD HKCU "${UNINSTALL_KEY}" "EstimatedSize" $0

  ; vpxmd.dll (the VP8 codec used for screen sharing) needs the Visual C++ runtime. The client works
  ; without it, but cannot share or watch screens, so offer the download instead of failing silently.
  ${IfNot} ${FileExists} "$SYSDIR\vcruntime140.dll"
    ${If} ${Cmd} `MessageBox MB_YESNO|MB_ICONINFORMATION "Screen sharing needs the Microsoft Visual C++ Redistributable (x64), which is not installed on this computer.$\r$\n$\r$\nOpen its download page now? You can also install it later." /SD IDNO IDYES`
      ExecShell "open" "${VC_REDIST_URL}"
    ${EndIf}
  ${EndIf}
SectionEnd

Section "Desktop shortcut" SecDesktop
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
SectionEnd

; /RUN starts Discagem once installed: the client passes it (with /S) when it updates itself.
Function .onInstSuccess
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/RUN" $1
  ${IfNot} ${Errors}
    Exec '"$INSTDIR\${APP_EXE}"'
  ${EndIf}
FunctionEnd

; ---------------------------------------------------------------------------------------------------
; Uninstaller

Function un.onInit
  ; Refuse to run from anywhere but a Discagem installation: the uninstaller deletes its folder.
  ${IfNot} ${FileExists} "$INSTDIR\Discagem.dll"
    MessageBox MB_OK|MB_ICONSTOP "${APP_NAME} was not found in $INSTDIR." /SD IDOK
    Abort
  ${EndIf}
FunctionEnd

Section "un.${APP_NAME}" UnSecApp
  SectionIn RO
  !insertmacro WaitForClientToClose

  Delete "$SMPROGRAMS\${APP_NAME}.lnk"
  Delete "$DESKTOP\${APP_NAME}.lnk"
  DeleteRegKey HKCU "${UNINSTALL_KEY}"
  ; Written by the client when "Start Discagem with Windows" is turned on.
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APP_NAME}"
  RMDir /r "$INSTDIR"
SectionEnd

Section /o "un.Settings and saved session" UnSecSettings
  RMDir /r "$APPDATA\${APP_NAME}"
SectionEnd
