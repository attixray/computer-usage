Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

Name "CompUsage"
OutFile "..\artifacts\release\CompUsage-1.0.3-Setup-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\CompUsage"
RequestExecutionLevel user
SetCompressor /SOLID lzma
VIProductVersion "1.0.3.0"
VIAddVersionKey "ProductName" "CompUsage"
VIAddVersionKey "FileDescription" "CompUsage Setup"
VIAddVersionKey "FileVersion" "1.0.3"
VIAddVersionKey "LegalCopyright" "CompUsage contributors"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\CompUsage.exe"
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"
!insertmacro MUI_LANGUAGE "Hungarian"

!macro CheckRunning
  System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\CompUsage") p .r0'
  ${If} $0 != 0
    System::Call 'kernel32::CloseHandle(p r0)'
    MessageBox MB_OK|MB_ICONEXCLAMATION "Please close CompUsage before continuing. / A folytatáshoz zárd be a CompUsage alkalmazást."
    Abort
  ${EndIf}
!macroend

!macro StopActivityMonitor
  ; Stop the session collector through its event, including one launched from a portable folder.
  ; Do not depend on an already installed executable or terminate an unrelated process by name.
  System::Call 'kernel32::GetCurrentProcessId() i .r1'
  System::Call 'kernel32::ProcessIdToSessionId(i r1, *i .r2)'
  StrCpy $3 0
  ${Do}
    System::Call 'kernel32::OpenMutexW(i 0x100000, i 0, w "Local\CompUsage.Activity.$2") p .r0'
    ${If} $0 == 0
      ${ExitDo}
    ${EndIf}
    System::Call 'kernel32::CloseHandle(p r0)'
    System::Call 'kernel32::OpenEventW(i 2, i 0, w "Local\CompUsage.Activity.$2.Stop") p .r0'
    ${If} $0 != 0
      System::Call 'kernel32::SetEvent(p r0)'
      System::Call 'kernel32::CloseHandle(p r0)'
    ${EndIf}
    IntOp $3 $3 + 1
    ${If} $3 >= 120
      MessageBox MB_OK|MB_ICONSTOP "The activity monitor could not stop. / Az aktivitásfigyelőt nem sikerült leállítani."
      Abort
    ${EndIf}
    Sleep 100
  ${Loop}
!macroend

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "CompUsage requires 64-bit Windows."
    Abort
  ${EndIf}
  !insertmacro MUI_LANGDLL_DISPLAY
  !insertmacro CheckRunning
FunctionEnd

Section "CompUsage"
  SetShellVarContext current
  !insertmacro StopActivityMonitor
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File /r "..\artifacts\publish\*.*"
  IfErrors 0 +3
    MessageBox MB_OK|MB_ICONSTOP "Installation failed. Close CompUsage and run Setup again."
    Abort
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\CompUsage"
  CreateShortcut "$SMPROGRAMS\CompUsage\CompUsage.lnk" "$INSTDIR\CompUsage.exe"
  CreateShortcut "$SMPROGRAMS\CompUsage\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CompUsage" "DisplayName" "CompUsage"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CompUsage" "DisplayVersion" "1.0.3"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CompUsage" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CompUsage" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CompUsage" "DisplayIcon" "$INSTDIR\CompUsage.exe"
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CompUsage" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CompUsage" "NoRepair" 1

  ; Use the official signed bootstrapper only when the shared runtime is absent.
  ReadRegStr $0 HKLM "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  ${EndIf}
  ${If} $0 == ""
  ${OrIf} $0 == "0.0.0.0"
    SetOutPath "$PLUGINSDIR"
    File "dependencies\MicrosoftEdgeWebview2Setup.exe"
    DetailPrint "Installing Microsoft WebView2 Runtime (internet connection required)..."
    ExecWait '"$PLUGINSDIR\MicrosoftEdgeWebview2Setup.exe" /silent /install' $0
    ${If} $0 != 0
      MessageBox MB_OK|MB_ICONEXCLAMATION "WebView2 installation did not finish. Run Setup again with an internet connection before starting CompUsage."
    ${EndIf}
  ${EndIf}
  ; Register the collector independently of the optional UI launch on the finish page.
  ; Its own configuration defaults to disabled, so no activity is collected until the user enables it.
  ClearErrors
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CompUsageActivity" '"$INSTDIR\CompUsage.exe" --activity-monitor'
  IfErrors 0 +3
    MessageBox MB_OK|MB_ICONSTOP "Could not register the activity monitor for Windows sign-in."
    Abort
  Exec '"$INSTDIR\CompUsage.exe" --activity-monitor'
SectionEnd

Function un.onInit
  !insertmacro CheckRunning
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  !insertmacro StopActivityMonitor
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "CompUsageActivity"
  ; Delete only the files listed by the build, not arbitrary user-created files or stored work data.
  !include "uninstall-files.nsh"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\CompUsage\CompUsage.lnk"
  Delete "$SMPROGRAMS\CompUsage\Uninstall.lnk"
  RMDir "$SMPROGRAMS\CompUsage"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\CompUsage"
SectionEnd
