[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'MSI customization requires Windows.'
}

$msiPath = (Resolve-Path -LiteralPath $Path).Path
if ([IO.Path]::GetExtension($msiPath) -ne '.msi') {
    throw "Expected an .msi file: $msiPath"
}

$languageBitmapPath = (Resolve-Path -LiteralPath (
        Join-Path $PSScriptRoot '..\assets\installer\msi-language.bmp')).Path

function Invoke-ComMethod {
    param(
        [Parameter(Mandatory = $true)] $Target,
        [Parameter(Mandatory = $true)] [string] $Name,
        [AllowNull()] [object[]] $Arguments
    )

    return $Target.GetType().InvokeMember(
        $Name,
        [Reflection.BindingFlags]::InvokeMethod,
        $null,
        $Target,
        $Arguments)
}

function ConvertFrom-Utf8Base64 {
    param(
        [Parameter(Mandatory = $true)] [string] $Value
    )

    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

function Invoke-MsiSql {
    param(
        [Parameter(Mandatory = $true)] $Database,
        [Parameter(Mandatory = $true)] [string] $Sql
    )

    $view = Invoke-ComMethod -Target $Database -Name 'OpenView' -Arguments @($Sql)
    try {
        try {
            [void](Invoke-ComMethod -Target $view -Name 'Execute' -Arguments $null)
        }
        catch {
            throw "Failed to execute MSI SQL: $Sql`n$($_.Exception.Message)"
        }
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

function Add-MsiBinaryStream {
    param(
        [Parameter(Mandatory = $true)] $Installer,
        [Parameter(Mandatory = $true)] $Database,
        [Parameter(Mandatory = $true)] [string] $Name,
        [Parameter(Mandatory = $true)] [string] $FilePath
    )

    $record = Invoke-ComMethod -Target $Installer -Name 'CreateRecord' -Arguments @(1)
    $view = $null
    try {
        [void](Invoke-ComMethod -Target $record -Name 'SetStream' -Arguments @(1, $FilePath))
        $sql = "INSERT INTO ``Binary`` (``Name``, ``Data``) VALUES ('$Name', ?)"
        $view = Invoke-ComMethod -Target $Database -Name 'OpenView' -Arguments @($sql)
        [void](Invoke-ComMethod -Target $view -Name 'Execute' -Arguments @($record))
    }
    finally {
        if ($null -ne $view) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
    }
}

function Test-MsiDialogExists {
    param(
        [Parameter(Mandatory = $true)] $Database,
        [Parameter(Mandatory = $true)] [string] $Dialog
    )

    $sql = "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog``='$Dialog'"
    $view = Invoke-ComMethod -Target $Database -Name 'OpenView' -Arguments @($sql)
    try {
        [void](Invoke-ComMethod -Target $view -Name 'Execute' -Arguments $null)
        $record = Invoke-ComMethod -Target $view -Name 'Fetch' -Arguments $null
        if ($null -eq $record) {
            return $false
        }

        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
        return $true
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

function Assert-MsiPerMachineElevationPolicy {
    param(
        [Parameter(Mandatory = $true)] $Database
    )

    # A PerMachine MSI has ALLUSERS=1 in the Property table. Older Either-scope
    # packages set it through the InstallScopeDlg control event instead.
    $sql = "SELECT ``Property`` FROM ``Property`` WHERE ``Property``='ALLUSERS' AND ``Value``='1'"
    $view = Invoke-ComMethod -Target $Database -Name 'OpenView' -Arguments @($sql)
    $record = $null
    try {
        [void](Invoke-ComMethod -Target $view -Name 'Execute' -Arguments $null)
        $record = Invoke-ComMethod -Target $view -Name 'Fetch' -Arguments $null
        if ($null -eq $record) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
            $view = Invoke-ComMethod -Target $Database -Name 'OpenView' -Arguments @(
                "SELECT ``Dialog_`` FROM ``ControlEvent`` WHERE ``Dialog_``='InstallScopeDlg' AND ``Control_``='Next' AND ``Event``='[ALLUSERS]' AND ``Argument``='1' AND ``Ordering`` < 8")
            [void](Invoke-ComMethod -Target $view -Name 'Execute' -Arguments $null)
            $record = Invoke-ComMethod -Target $view -Name 'Fetch' -Arguments $null
        }
        if ($null -eq $record) {
            throw 'Velopack MSI is not configured as a per-machine installer (ALLUSERS=1); it would not reliably request elevation.'
        }
    }
    finally {
        if ($null -ne $record) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
        }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

function Get-VelopackLocaleOverrides {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet('en-US', 'zh-CN')]
        [string] $Locale
    )

    $toolStore = Join-Path $env:USERPROFILE '.dotnet\tools\.store\vpk\1.2.0'
    $nativeModule = Get-ChildItem -LiteralPath $toolStore -Recurse -Filter 'velopack_wix_x64.dll' |
        Select-Object -First 1
    if ($null -eq $nativeModule) {
        throw "Velopack 1.2.0 localization module was not found under: $toolStore"
    }

    $moduleText = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($nativeModule.FullName))
    $propertyMap = @{}
    [regex]::Matches($moduleText, '(?<property>Msi[A-Z][A-Za-z0-9]+?)(?<key>msi-[a-z0-9-]+)') |
        ForEach-Object {
            $propertyMap[$_.Groups['key'].Value] = $_.Groups['property'].Value
        }

    $blockStarts = [regex]::Matches(
        $moduleText,
        'msi-btn-back = .*?\r\nmsi-btn-next = .*?\r\n') |
        ForEach-Object { $_.Index }
    $blocks = for ($i = 0; $i -lt $blockStarts.Count; $i++) {
        $end = if ($i + 1 -lt $blockStarts.Count) { $blockStarts[$i + 1] } else { $moduleText.Length }
        $moduleText.Substring($blockStarts[$i], $end - $blockStarts[$i])
    }

    $localeBlock = if ($Locale -eq 'en-US') {
        $blocks |
            Where-Object { $_.Contains('msi-welcome-title = Welcome to the { $app_title } Setup Wizard') } |
            Select-Object -First 1
    }
    else {
        $nextMarker = ConvertFrom-Utf8Base64 'bXNpLWJ0bi1uZXh0ID0g5LiL5LiA5q2lKCZOKQ=='
        $welcomeMarker = ConvertFrom-Utf8Base64 'bXNpLXdlbGNvbWUtdGl0bGUgPSDmrKLov47kvb/nlKg='
        $blocks |
            Where-Object {
                $_.Contains($nextMarker) -and
                $_.Contains($welcomeMarker)
            } |
            Select-Object -First 1
    }

    if ([string]::IsNullOrEmpty($localeBlock)) {
        throw "Unable to extract the Velopack $Locale MSI localization block."
    }

    $overrides = [ordered]@{}
    $entryPattern = '(?m)^(?<key>msi-[a-z0-9-]+) = (?<value>[^\r\n]*)(?<continuations>(?:\r?\n    [^\r\n]*)*)'
    foreach ($entry in [regex]::Matches($localeBlock, $entryPattern)) {
        $key = $entry.Groups['key'].Value
        if (-not $propertyMap.ContainsKey($key)) {
            continue
        }

        $value = $entry.Groups['value'].Value +
            ($entry.Groups['continuations'].Value -replace '\r?\n    ', ' ')
        $value = $value.Replace('{ $app_title }', '[RustAppTitle]')
        $value = $value.Replace('{ $app_version }', '[RustAppVersion]')
        if ($value -match '\{ \$') {
            throw "Unsupported dynamic variable in $Locale localization key '$key': $value"
        }

        $overrides[$propertyMap[$key]] = $value
    }

    # Velopack 1.2.0's embedded locale blocks have a mismatched msi-dlg-title
    # boundary. The language buttons set MsiDlgTitle explicitly below.
    [void] $overrides.Remove('MsiDlgTitle')

    if ($overrides.Count -ne 102) {
        throw "Expected 102 Velopack $Locale MSI properties, extracted $($overrides.Count)."
    }

    return $overrides
}

function ConvertTo-MsiSqlString {
    param([AllowEmptyString()] [string] $Value)

    return $Value.Replace("'", "''")
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null

try {
    # msiOpenDatabaseModeTransact = 1
    $database = Invoke-ComMethod -Target $installer -Name 'OpenDatabase' -Arguments @($msiPath, 1)
    Assert-MsiPerMachineElevationPolicy -Database $database

    $hasInstallDirDialog = Test-MsiDialogExists -Database $database -Dialog 'InstallDirDlg'
    $hasLanguageDialog = Test-MsiDialogExists -Database $database -Dialog 'LanguageDlg'
    if ($hasInstallDirDialog -and $hasLanguageDialog) {
        Write-Host "MSI already contains language and install directory dialogs: $msiPath"
        return
    }
    if ($hasInstallDirDialog -or $hasLanguageDialog) {
        throw 'MSI is only partially customized. Rebuild it with Velopack before running this script again.'
    }

    $englishOverrides = Get-VelopackLocaleOverrides -Locale 'en-US'
    $chineseOverrides = Get-VelopackLocaleOverrides -Locale 'zh-CN'
    Add-MsiBinaryStream -Installer $installer -Database $database `
        -Name 'EasyChat_Language_Dialog_Bmp' -FilePath $languageBitmapPath

    $languageText = @{
        Setup = ConvertFrom-Utf8Base64 'RWFzeUNoYXQg5a6J6KOF'
        Choose = ConvertFrom-Utf8Base64 '6YCJ5oup5a6J6KOF6K+t6KiA'
        Select = ConvertFrom-Utf8Base64 '6K+36YCJ5oup5a6J6KOF6K+t6KiA44CC'
        Chinese = ConvertFrom-Utf8Base64 '566A5L2T5Lit5paH'
        Cancel = ConvertFrom-Utf8Base64 '5Y+W5raI'
        InstallTitle = ConvertFrom-Utf8Base64 '5a6J6KOF56iL5bqP'
    }

    $statements = @(
        # Velopack 1.2.0 writes this localization resource key into the shortcut
        # comment field instead of resolving it, which Windows shows on hover.
        'UPDATE `Shortcut` SET `Description` = ''EasyChat'' WHERE `Description` = ''MsiDesktopShortcutDescription''',
        'UPDATE `Shortcut` SET `Description` = ''EasyChat'' WHERE `Description` = ''[MsiDesktopShortcutDescription]''',
        'UPDATE `Shortcut` SET `Description` = ''EasyChat'' WHERE `Description` = ''MsiStartMenuShortcutDescription''',
        'UPDATE `Shortcut` SET `Description` = ''EasyChat'' WHERE `Description` = ''[MsiStartMenuShortcutDescription]''',
        'INSERT INTO `Dialog` (`Dialog`, `HCentering`, `VCentering`, `Width`, `Height`, `Attributes`, `Title`, `Control_First`, `Control_Default`, `Control_Cancel`) VALUES (''LanguageDlg'', 50, 50, 370, 270, 7, ''EasyChat Setup / __ZH_SETUP__'', ''Chinese'', ''Chinese'', ''Cancel'')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''LanguageDlg'', ''DialogBitmap'', ''Bitmap'', 0, 0, 115, 234, 1, '''', ''EasyChat_Language_Dialog_Bmp'', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''LanguageDlg'', ''BottomLine'', ''Line'', 0, 234, 370, 0, 1, '''', '''', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''LanguageDlg'', ''Title'', ''Text'', 140, 20, 215, 40, 196611, '''', ''Choose setup language / __ZH_CHOOSE__'', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''LanguageDlg'', ''Description'', ''Text'', 140, 65, 215, 30, 196611, '''', ''Select a language to continue. / __ZH_SELECT__'', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''LanguageDlg'', ''Chinese'', ''PushButton'', 140, 105, 95, 24, 3, '''', ''__ZH_CHINESE__'', ''English'', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''LanguageDlg'', ''English'', ''PushButton'', 245, 105, 95, 24, 3, '''', ''English'', ''Cancel'', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''LanguageDlg'', ''Cancel'', ''PushButton'', 280, 243, 80, 17, 3, '''', ''Cancel / __ZH_CANCEL__'', ''Chinese'', '''')',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''LanguageDlg'', ''Cancel'', ''SpawnDialog'', ''CancelDlg'', ''1'', 1)',

        'INSERT INTO `Dialog` (`Dialog`, `HCentering`, `VCentering`, `Width`, `Height`, `Attributes`, `Title`, `Control_First`, `Control_Default`, `Control_Cancel`) VALUES (''InstallDirDlg'', 50, 50, 370, 270, 7, ''[MsiDlgTitle]'', ''InstallDirEdit'', ''Next'', ''Cancel'')',

        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''BannerBitmap'', ''Bitmap'', 0, 0, 370, 44, 1, '''', ''WixUI_Bmp_Banner'', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''BannerLine'', ''Line'', 0, 44, 370, 0, 1, '''', '''', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''BottomLine'', ''Line'', 0, 234, 370, 0, 1, '''', '''', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''Title'', ''Text'', 15, 6, 200, 15, 196611, '''', ''[MsiBrowseTitle]'', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''Description'', ''Text'', 25, 23, 280, 15, 196611, '''', ''[MsiBrowseDescription]'', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''PathLabel'', ''Text'', 25, 70, 320, 10, 3, '''', ''[MsiBrowsePathLabel]'', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''InstallDirEdit'', ''PathEdit'', 25, 84, 264, 18, 11, ''WIXUI_INSTALLDIR'', '''', ''Browse'', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''Browse'', ''PushButton'', 296, 84, 56, 18, 3, '''', ''[MsiReadyBtnChange]'', ''Back'', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''Back'', ''PushButton'', 180, 243, 56, 17, 3, '''', ''[MsiBtnBack]'', ''Next'', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''Next'', ''PushButton'', 236, 243, 56, 17, 3, '''', ''[MsiBtnNext]'', ''Cancel'', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallDirDlg'', ''Cancel'', ''PushButton'', 304, 243, 56, 17, 3, '''', ''[MsiBtnCancel]'', ''InstallDirEdit'', '''')',

        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallDirDlg'', ''Browse'', ''[_BrowseProperty]'', ''[WIXUI_INSTALLDIR]'', ''1'', 1)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallDirDlg'', ''Browse'', ''NewDialog'', ''BrowseDlg'', ''1'', 2)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallDirDlg'', ''Back'', ''NewDialog'', ''InstallScopeDlg'', ''1'', 1)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallDirDlg'', ''Next'', ''SetTargetPath'', ''[WIXUI_INSTALLDIR]'', ''1'', 1)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallDirDlg'', ''Next'', ''NewDialog'', ''VerifyReadyDlg'', ''1'', 2)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallDirDlg'', ''Cancel'', ''SpawnDialog'', ''CancelDlg'', ''1'', 1)',

        'DELETE FROM `ControlEvent` WHERE `Dialog_`=''BrowseDlg'' AND `Control_`=''OK'' AND `Event`=''DoAction'' AND `Argument`=''RustValidatePath''',
        'DELETE FROM `ControlEvent` WHERE `Dialog_`=''BrowseDlg'' AND `Control_`=''OK'' AND `Event`=''SpawnDialog'' AND `Argument`=''InvalidDirDlg''',
        'DELETE FROM `ControlEvent` WHERE `Dialog_`=''BrowseDlg'' AND `Control_`=''OK'' AND `Event`=''EndDialog'' AND `Argument`=''Return''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''BrowseDlg'', ''OK'', ''NewDialog'', ''InstallDirDlg'', ''1'', 2)',
        'DELETE FROM `ControlEvent` WHERE `Dialog_`=''BrowseDlg'' AND `Control_`=''Cancel'' AND `Event`=''EndDialog'' AND `Argument`=''Return''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''BrowseDlg'', ''Cancel'', ''NewDialog'', ''InstallDirDlg'', ''1'', 2)',

        'DELETE FROM `ControlEvent` WHERE `Dialog_`=''InstallScopeDlg'' AND `Control_`=''Next'' AND `Event`=''NewDialog'' AND `Argument`=''VerifyReadyDlg''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''Next'', ''NewDialog'', ''InstallDirDlg'', ''1'', 8)',
        'DELETE FROM `ControlEvent` WHERE `Dialog_`=''VerifyReadyDlg'' AND `Control_`=''Back'' AND `Event`=''NewDialog'' AND `Argument`=''InstallScopeDlg''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''VerifyReadyDlg'', ''Back'', ''NewDialog'', ''InstallDirDlg'', ''NOT Installed'', 1)',

        'DELETE FROM `InstallUISequence` WHERE `Action`=''WelcomeDlg''',
        'INSERT INTO `InstallUISequence` (`Action`, `Condition`, `Sequence`) VALUES (''LanguageDlg'', ''NOT Installed OR PATCH'', 1297)'
    )

    $statements = $statements | ForEach-Object {
        $_.Replace('__ZH_SETUP__', $languageText.Setup).
            Replace('__ZH_CHOOSE__', $languageText.Choose).
            Replace('__ZH_SELECT__', $languageText.Select).
            Replace('__ZH_CHINESE__', $languageText.Chinese).
            Replace('__ZH_CANCEL__', $languageText.Cancel)
    }

    foreach ($statement in $statements) {
        Invoke-MsiSql -Database $database -Sql $statement
    }

    $ordering = 1
    foreach ($property in $englishOverrides.Keys) {
        $argument = ConvertTo-MsiSqlString -Value $englishOverrides[$property]
        $statement = "INSERT INTO ``ControlEvent`` (``Dialog_``, ``Control_``, ``Event``, ``Argument``, ``Condition``, ``Ordering``) VALUES ('LanguageDlg', 'English', '[$property]', '$argument', '1', $ordering)"
        Invoke-MsiSql -Database $database -Sql $statement
        $ordering++
    }
    Invoke-MsiSql -Database $database -Sql "INSERT INTO ``ControlEvent`` (``Dialog_``, ``Control_``, ``Event``, ``Argument``, ``Condition``, ``Ordering``) VALUES ('LanguageDlg', 'English', '[MsiDlgTitle]', '[RustAppTitle] Setup', '1', $ordering)"
    $ordering++
    Invoke-MsiSql -Database $database -Sql "INSERT INTO ``ControlEvent`` (``Dialog_``, ``Control_``, ``Event``, ``Argument``, ``Condition``, ``Ordering``) VALUES ('LanguageDlg', 'English', 'NewDialog', 'WelcomeDlg', '1', $ordering)"

    $ordering = 1
    foreach ($property in $chineseOverrides.Keys) {
        $argument = ConvertTo-MsiSqlString -Value $chineseOverrides[$property]
        $statement = "INSERT INTO ``ControlEvent`` (``Dialog_``, ``Control_``, ``Event``, ``Argument``, ``Condition``, ``Ordering``) VALUES ('LanguageDlg', 'Chinese', '[$property]', '$argument', '1', $ordering)"
        Invoke-MsiSql -Database $database -Sql $statement
        $ordering++
    }
    $chineseTitle = ConvertTo-MsiSqlString -Value ("[RustAppTitle] " + $languageText.InstallTitle)
    Invoke-MsiSql -Database $database -Sql "INSERT INTO ``ControlEvent`` (``Dialog_``, ``Control_``, ``Event``, ``Argument``, ``Condition``, ``Ordering``) VALUES ('LanguageDlg', 'Chinese', '[MsiDlgTitle]', '$chineseTitle', '1', $ordering)"
    $ordering++
    Invoke-MsiSql -Database $database -Sql "INSERT INTO ``ControlEvent`` (``Dialog_``, ``Control_``, ``Event``, ``Argument``, ``Condition``, ``Ordering``) VALUES ('LanguageDlg', 'Chinese', 'NewDialog', 'WelcomeDlg', '1', $ordering)"

    [void](Invoke-ComMethod -Target $database -Name 'Commit' -Arguments $null)
    Write-Host "Added language and install directory selection to: $msiPath"
}
finally {
    if ($null -ne $database) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }
    [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
