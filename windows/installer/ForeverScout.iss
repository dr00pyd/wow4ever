; Forever Scout – installationsprogram (Inno Setup 6.7+).
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" client\windows\installer\ForeverScout.iss
; Kräver att lite-varianten är byggd först (dist\lite\ForeverScout.exe, se client\windows\README.md).
; Utdata: client\windows\dist\setup\ForeverScout-Setup-<version>.exe
;
; - Installeras per användare utan admin i %LOCALAPPDATA%\Programs\Forever Scout (självuppdateringen fungerar då utan UAC).
; - Innehåller lite-exe:n (ramverksberoende). Saknas .NET 10 Desktop Runtime erbjuds att hämta Microsofts officiella
;   installationsprogram över HTTPS (aka.ms → builds.dotnet.microsoft.com) – det steget ber om UAC, och användaren får
;   veta det innan. Avböjer man visas länken. Filen startas bara om dess Authenticode-signatur är giltig och kommer från
;   Microsoft Corporation (M7); annars öppnas Microsofts nedladdningssida i stället.
; - Fast AppId: nya versioner uppgraderar på plats. En körande Forever Scout stängs före installationen.
; - Avinstalleraren tar bort exe, genvägar och autostartvärdet, och frågar (standard Nej) om %APPDATA%\ForeverScout också ska bort.

; /DExePath=... kan peka på en annan exe (används bara för att testa uppgradering från en äldre version).
#ifndef ExePath
  #define ExePath AddBackslash(SourcePath) + "..\dist\lite\ForeverScout.exe"
#endif
#if !FileExists(ExePath)
  #error Bygg lite-varianten först: dotnet publish src\ForeverScout -c Release -p:ScoutVariant=lite -o dist\lite
#endif
#define FileVer GetVersionNumbersString(ExePath)
#define AppVersion Copy(FileVer, 1, RPos(".", FileVer) - 1)

[Setup]
; Ändra ALDRIG AppId – den kopplar ihop alla versioner (uppgradering på plats, en post under Appar).
AppId={{8F3C2B1E-6A4D-4E7B-9C1F-2D5A7B9E0C34}
AppName=Forever Scout
AppVersion={#AppVersion}
AppVerName=Forever Scout {#AppVersion}
AppPublisher=Forever-guiden (guide.wow4ever.se)
AppPublisherURL=https://guide.wow4ever.se
AppSupportURL=https://guide.wow4ever.se/bidra
AppUpdatesURL=https://guide.wow4ever.se/bidra/ladda-ner
VersionInfoVersion={#FileVer}
VersionInfoCompany=Forever-guiden (guide.wow4ever.se)
VersionInfoProductName=Forever Scout
VersionInfoDescription=Forever Scout – installationsprogram
VersionInfoCopyright=Forever-guiden 2026
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
DefaultDirName={localappdata}\Programs\Forever Scout
DisableDirPage=yes
DefaultGroupName=Forever Scout
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
OutputDir=..\dist\setup
OutputBaseFilename=ForeverScout-Setup-{#AppVersion}
SetupIconFile=..\src\ForeverScout\app.ico
UninstallDisplayIcon={app}\ForeverScout.exe
UninstallDisplayName=Forever Scout
Compression=lzma2/max
SolidCompression=yes
; Lägereld: mörk stil, egen bakgrundsfärg och bilder med logotyp och lägereld.
WizardStyle=modern dark
WizardBackColor=#17151A
WizardImageFile=wizard-large-100.png,wizard-large-150.png,wizard-large-200.png
WizardSmallImageFile=wizard-small-100.png,wizard-small-150.png,wizard-small-200.png
WizardImageBackColor=#17151A
ShowLanguageDialog=no
LanguageDetectionMethod=uilanguage
; Uppgradering: stäng en körande Forever Scout (Restart Manager) och kontrollera appens mutex (Program.cs/AppBehavior.cs).
CloseApplications=force
RestartApplications=no
AppMutex=Local\ForeverScout.SingleInstance

[Languages]
Name: "swedish"; MessagesFile: "compiler:Languages\Swedish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
swedish.LaunchNow=Starta Forever Scout nu
english.LaunchNow=Start Forever Scout now
swedish.DesktopIcon=Skapa en genväg på skrivbordet
english.DesktopIcon=Create a desktop shortcut
swedish.RuntimeAsk=Forever Scout behöver Microsoft .NET 10 Desktop Runtime, som inte finns på datorn.%n%nVill du att installationen hämtar Microsofts officiella installationsprogram (från dotnet.microsoft.com, ca 60 MB) och installerar det nu?%n%nWindows kommer att be om administratörsbehörighet för just det steget.
english.RuntimeAsk=Forever Scout needs Microsoft .NET 10 Desktop Runtime, which is not installed.%n%nDo you want setup to download Microsoft's official installer (from dotnet.microsoft.com, about 60 MB) and install it now?%n%nWindows will ask for administrator permission for that step only.
swedish.RuntimeManual=Installera .NET 10 Desktop Runtime (Windows x64) själv från Microsoft innan du startar Forever Scout:%n%n%1%n%nVill du öppna sidan nu?
english.RuntimeManual=Please install .NET 10 Desktop Runtime (Windows x64) from Microsoft before starting Forever Scout:%n%n%1%n%nOpen the page now?
swedish.RuntimeFailed=Installationen av .NET 10 Desktop Runtime avbröts eller misslyckades (kod %1).
english.RuntimeFailed=Installing .NET 10 Desktop Runtime was cancelled or failed (code %1).
swedish.RuntimeUnsigned=Det hämtade installationsprogrammet för .NET 10 Desktop Runtime har ingen giltig signatur från Microsoft och startas därför inte.%n%nMicrosofts nedladdningssida öppnas i stället – installera .NET 10 Desktop Runtime (Windows x64) därifrån innan du startar Forever Scout.
english.RuntimeUnsigned=The downloaded .NET 10 Desktop Runtime installer does not have a valid Microsoft signature, so it will not be started.%n%nMicrosoft's download page opens instead – install .NET 10 Desktop Runtime (Windows x64) from there before starting Forever Scout.
swedish.RuntimeDownloading=Hämtar .NET 10 Desktop Runtime från Microsoft …
english.RuntimeDownloading=Downloading .NET 10 Desktop Runtime from Microsoft …
swedish.UninstallData=Vill du även ta bort Forever Scouts inställningar, nyckel och logg (%APPDATA%\ForeverScout)?%n%nVälj Nej om du vill behålla dem till en senare installation.
english.UninstallData=Do you also want to remove Forever Scout's settings, key and log (%APPDATA%\ForeverScout)?%n%nChoose No to keep them for a later installation.

[Tasks]
; Markerad som standard.
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#ExePath}"; DestDir: "{app}"; DestName: "ForeverScout.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\Forever Scout"; Filename: "{app}\ForeverScout.exe"; WorkingDir: "{app}"
Name: "{userdesktop}\Forever Scout"; Filename: "{app}\ForeverScout.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ForeverScout.exe"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Självuppdateringens tillfälliga filer bredvid exe:n.
Type: files; Name: "{app}\ForeverScout.new.exe"
Type: files; Name: "{app}\ForeverScout.old.exe"
Type: files; Name: "{app}\ForeverScout.new.exe.tmp"
Type: dirifempty; Name: "{app}"

[Code]
const
  RuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  RuntimePage = 'https://dotnet.microsoft.com/download/dotnet/10.0';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  DownloadPage: TDownloadWizardPage;
  WantRuntime: Boolean;
  RuntimeFile: String;

{ Finns Microsoft.WindowsDesktop.App 10.x? Kollar både registret (det dotnet-installationen skriver) och mappen. }
function DesktopRuntime10Installed(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  FindRec: TFindRec;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then Result := True;
  if not Result then
    if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
    begin
      Result := True;
      FindClose(FindRec);
    end;
end;

procedure InitializeWizard();
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), CustomMessage('RuntimeDownloading'), nil);
end;

procedure OfferManualRuntime();
var
  Code: Integer;
begin
  if SuppressibleMsgBox(FmtMessage(CustomMessage('RuntimeManual'), [RuntimePage]), mbInformation, MB_YESNO, IDNO) = IDYES then
    ShellExecAsOriginalUser('open', RuntimePage, '', '', SW_SHOWNORMAL, ewNoWait, Code);
end;

{ På sidan "Klar att installera": saknas runtime – fråga (och berätta om UAC) innan något hämtas. }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and (not WizardSilent()) and (not DesktopRuntime10Installed()) then
  begin
    WantRuntime := MsgBox(CustomMessage('RuntimeAsk'), mbConfirmation, MB_YESNO) = IDYES;
    if WantRuntime then
    begin
      DownloadPage.Clear;
      DownloadPage.Add(RuntimeUrl, 'windowsdesktop-runtime-10-win-x64.exe', '');
      DownloadPage.Show;
      try
        try
          DownloadPage.Download;
          RuntimeFile := ExpandConstant('{tmp}\windowsdesktop-runtime-10-win-x64.exe');
        except
          if not DownloadPage.AbortedByUser then SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
          WantRuntime := False;
        end;
      finally
        DownloadPage.Hide;
      end;
    end;
    if not WantRuntime then OfferManualRuntime();
  end;
end;

{ M7: är filen signerad av Microsoft? Get-AuthenticodeSignature (PowerShell 5.1, finns i alla Windows 10/11) måste ge
  Status = Valid (kedjan till en betrodd rot, inte återkallad, filen orörd), och både certifikatets ägare och utfärdare
  ska ha O=Microsoft Corporation (dagens runtime: "CN=.NET, O=Microsoft Corporation, …", utfärdad av "Microsoft Code
  Signing PCA 2024, O=Microsoft Corporation"). Allt annat – även att PowerShell saknas eller blockeras – räknas som nej. }
function RuntimeSignedByMicrosoft(const FileName: String): Boolean;
var
  Code: Integer;
  P, Script: String;
begin
  Result := False;
  P := FileName;
  StringChangeEx(P, '''', '''''', True); { enkelfnutt i sökvägen (t.ex. O'Brien) dubbleras för PowerShell }
  Script := '$s = Get-AuthenticodeSignature -LiteralPath ''' + P + '''; ' +
    'if ($s.Status -eq ''Valid'' -and $s.SignerCertificate.Subject -match ''(^|, )O=Microsoft Corporation(,|$)'' ' +
    '-and $s.SignerCertificate.Issuer -match ''(^|, )O=Microsoft Corporation(,|$)'') { exit 0 } else { exit 1 }';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
          '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"',
          '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := (Code = 0);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Lock: TFileStream;
begin
  if (CurStep = ssPostInstall) and WantRuntime and (RuntimeFile <> '') then
  begin
    { M7: filen ligger i Setups tillfälliga katalog, som användarens egna processer kan skriva i. Den hålls öppen med skrivskydd
      (fmShareDenyWrite: kan inte ändras, döpas om eller raderas) från signaturkontrollen tills installationen är klar. }
    Lock := nil;
    try
      try
        Lock := TFileStream.Create(RuntimeFile, fmOpenRead or fmShareDenyWrite);
      except
        Lock := nil;
      end;
      if (Lock = nil) or (not RuntimeSignedByMicrosoft(RuntimeFile)) then
      begin
        { Ingen giltig Microsoft-signatur: kör den inte – öppna Microsofts nedladdningssida i stället. }
        SuppressibleMsgBox(CustomMessage('RuntimeUnsigned'), mbError, MB_OK, IDOK);
        ShellExecAsOriginalUser('open', RuntimePage, '', '', SW_SHOWNORMAL, ewNoWait, Code);
      end
      { Microsofts eget, signerade installationsprogram – kräver admin (UAC). }
      else if not ShellExec('runas', RuntimeFile, '/install /passive /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) or ((Code <> 0) and (Code <> 3010)) then
      begin
        SuppressibleMsgBox(FmtMessage(CustomMessage('RuntimeFailed'), [IntToStr(Code)]), mbError, MB_OK, IDOK);
        OfferManualRuntime();
      end;
    finally
      if Lock <> nil then Lock.Free;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    { Autostart ("Starta med Windows") – värdet som appen själv skriver. }
    RegDeleteValue(HKCU, RunKey, 'ForeverScout');
  if CurUninstallStep = usPostUninstall then
    if SuppressibleMsgBox(CustomMessage('UninstallData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
      DelTree(ExpandConstant('{userappdata}\ForeverScout'), True, True, True);
end;
