[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputRoot = "",
    [string]$DistributionName = "Hotkey-Translator-online",
    [switch]$SkipNativeBuild,
    [switch]$SkipDotnetPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$OutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "dist"
}
else {
    $OutputRoot
}
$distributionRoot = Join-Path $OutputRoot $DistributionName
$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishRoot = Join-Path $artifactsRoot "publish"
$mainPublishDir = Join-Path $publishRoot "main"
$helperPublishDir = Join-Path $publishRoot "elevator"
$nativeStageRoot = Join-Path $artifactsRoot "native-staging"
$nativeStageX64Dir = Join-Path $nativeStageRoot "x64"
$nativeStageX86Dir = Join-Path $nativeStageRoot "x86"
$script:missingHookArtifacts = [System.Collections.Generic.List[string]]::new()

function Write-Step {
    param([string]$Message)

    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-External {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [string]$WorkingDirectory = $repoRoot
    )

    Push-Location $WorkingDirectory
    try {
        Write-Host ">> $FilePath $($Arguments -join ' ')"
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $FilePath $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Reset-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path -LiteralPath $Path) {
        Get-ChildItem -LiteralPath $Path -Force | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }
    }
    else {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Copy-File {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $destinationDirectory = Split-Path -Path $Destination -Parent
    if (-not [string]::IsNullOrWhiteSpace($destinationDirectory)) {
        Ensure-Directory -Path $destinationDirectory
    }

    $attempts = 0
    $maxAttempts = 10
    while ($true) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            return
        }
        catch {
            $attempts++
            if ($attempts -ge $maxAttempts) {
                throw
            }

            Start-Sleep -Milliseconds 500
        }
    }
}

function Test-AnyWildcardMatch {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [string[]]$Patterns = @()
    )

    foreach ($pattern in $Patterns) {
        if ($Value -like $pattern) {
            return $true
        }
    }

    return $false
}

function Get-RelativeChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$RootPath,
        [Parameter(Mandatory = $true)][string]$ChildPath
    )

    $normalizedRoot = [System.IO.Path]::GetFullPath($RootPath).TrimEnd("\")
    $normalizedChild = [System.IO.Path]::GetFullPath($ChildPath)
    if ($normalizedChild.Length -lt $normalizedRoot.Length) {
        throw "Child path is not under the expected root. Root=$normalizedRoot Child=$normalizedChild"
    }

    if ($normalizedChild.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return ""
    }

    if (-not $normalizedChild.StartsWith("$normalizedRoot\", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Child path is not under the expected root. Root=$normalizedRoot Child=$normalizedChild"
    }

    return $normalizedChild.Substring($normalizedRoot.Length + 1)
}

function Copy-FilteredTree {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [string[]]$ExcludedDirectoryNames = @(),
        [string[]]$ExcludedFilePatterns = @(),
        [string[]]$ExcludedRelativeDirectoryPatterns = @(),
        [string[]]$ExcludedRelativeFilePatterns = @()
    )

    $sourceRoot = (Resolve-Path -LiteralPath $Source).Path
    Ensure-Directory -Path $Destination

    $pendingDirectories = [System.Collections.Generic.Queue[string]]::new()
    $pendingDirectories.Enqueue($sourceRoot)

    while ($pendingDirectories.Count -gt 0) {
        $currentDirectory = $pendingDirectories.Dequeue()
        $relativeDirectory = Get-RelativeChildPath -RootPath $sourceRoot -ChildPath $currentDirectory

        foreach ($directory in Get-ChildItem -LiteralPath $currentDirectory -Force -Directory) {
            $childRelativePath = if ([string]::IsNullOrEmpty($relativeDirectory)) {
                $directory.Name
            }
            else {
                Join-Path $relativeDirectory $directory.Name
            }

            if ($ExcludedDirectoryNames -contains $directory.Name) {
                continue
            }

            if (Test-AnyWildcardMatch -Value $childRelativePath -Patterns $ExcludedRelativeDirectoryPatterns) {
                continue
            }

            $pendingDirectories.Enqueue($directory.FullName)
        }

        foreach ($file in Get-ChildItem -LiteralPath $currentDirectory -Force -File) {
            $relativeFilePath = if ([string]::IsNullOrEmpty($relativeDirectory)) {
                $file.Name
            }
            else {
                Join-Path $relativeDirectory $file.Name
            }

            if (Test-AnyWildcardMatch -Value $file.Name -Patterns $ExcludedFilePatterns) {
                continue
            }

            if (Test-AnyWildcardMatch -Value $relativeFilePath -Patterns $ExcludedRelativeFilePatterns) {
                continue
            }

            $destinationPath = Join-Path $Destination $relativeFilePath
            Copy-File -Source $file.FullName -Destination $destinationPath
        }
    }
}

function Assert-PathExists {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description was not found: $Path"
    }
}

function Assert-OptionalPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        Write-Warning "$Description is missing and will not be packaged: $Path"
        return $false
    }

    return $true
}

function Save-ArtifactIfPresent {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$Description,
        [switch]$Required
    )

    if (Test-Path -LiteralPath $Source) {
        try {
            Copy-File -Source $Source -Destination $Destination
            return $true
        }
        catch {
            if ($Required) {
                throw
            }

            $script:missingHookArtifacts.Add("$Description => $Source")
            Write-Warning "$Description disappeared before packaging and will not be included: $Source"
            return $false
        }
    }

    if ($Required) {
        throw "$Description was not found: $Source"
    }

    $script:missingHookArtifacts.Add("$Description => $Source")
    Write-Warning "$Description is missing and will not be packaged: $Source"
    return $false
}

$serviceExcludedDirectories = @(
    ".venv",
    "__pycache__",
    ".pytest_cache",
    ".mypy_cache",
    ".ruff_cache",
    ".TransTest",
    ".testpicture",
    "out"
)

$serviceExcludedFiles = @(
    "*.pyc",
    "*.pyo",
    "*.log",
    "README.md",
    ".python-version",
    ".uv-sync.state",
    "test*.py",
    "*_test.py",
    "test*.png",
    "test*.jpg",
    "test*.jpeg",
    "test*.bmp"
)

Write-Step "Checking required tooling"
Get-Command dotnet -ErrorAction Stop | Out-Null
Get-Command cmake -ErrorAction Stop | Out-Null
Reset-Directory -Path $nativeStageX64Dir
Reset-Directory -Path $nativeStageX86Dir

if (-not $SkipDotnetPublish) {
    Write-Step "Publishing Hotkey-Translator single-file executable"
    Reset-Directory -Path $mainPublishDir
    Invoke-External -FilePath "dotnet" -Arguments @(
        "publish",
        (Join-Path $repoRoot "Hotkey-Translator.csproj"),
        "-c", $Configuration,
        "-r", $RuntimeIdentifier,
        "-p:PublishSingleFile=true",
        "-p:SelfContained=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishTrimmed=false",
        "-o", $mainPublishDir
    )

    Write-Step "Publishing WinRtLanguagePackElevator helper"
    Reset-Directory -Path $helperPublishDir
    Invoke-External -FilePath "dotnet" -Arguments @(
        "publish",
        (Join-Path $repoRoot "Tools\WinRtLanguagePackElevator\WinRtLanguagePackElevator.csproj"),
        "-c", $Configuration,
        "-r", $RuntimeIdentifier,
        "-p:PublishSingleFile=true",
        "-p:SelfContained=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:PublishTrimmed=false",
        "-o", $helperPublishDir
    )
}

if (-not $SkipNativeBuild) {
    Write-Step "Configuring and building native hook binaries (x64)"
    Invoke-External -FilePath "cmake" -Arguments @(
        "-S", (Join-Path $repoRoot "Native"),
        "-B", (Join-Path $repoRoot "Native\build"),
        "-A", "x64"
    )
    Invoke-External -FilePath "cmake" -Arguments @(
        "--build", (Join-Path $repoRoot "Native\build"),
        "--config", $Configuration
    )

    Write-Step "Staging x64 native hook outputs before x86 build"
    [void](Save-ArtifactIfPresent -Source (Join-Path $repoRoot "Native\HookHost\bin\HookHost.exe") -Destination (Join-Path $nativeStageX64Dir "HookHost.exe") -Description "x64 HookHost" -Required)
    [void](Save-ArtifactIfPresent -Source (Join-Path $repoRoot "Native\HookHost\bin\HookAgentDx9.dll") -Destination (Join-Path $nativeStageX64Dir "HookAgentDx9.dll") -Description "x64 HookAgentDx9")
    [void](Save-ArtifactIfPresent -Source (Join-Path $repoRoot "Native\HookHost\bin\HookAgentDx11.dll") -Destination (Join-Path $nativeStageX64Dir "HookAgentDx11.dll") -Description "x64 HookAgentDx11")
    [void](Save-ArtifactIfPresent -Source (Join-Path $repoRoot "Native\HookHost\bin\HookAgentVulkan.dll") -Destination (Join-Path $nativeStageX64Dir "HookAgentVulkan.dll") -Description "x64 HookAgentVulkan")

    Write-Step "Configuring and building native hook binaries (x86)"
    Invoke-External -FilePath "cmake" -Arguments @(
        "-S", (Join-Path $repoRoot "Native"),
        "-B", (Join-Path $repoRoot "Native\build_x86"),
        "-A", "Win32"
    )
    Invoke-External -FilePath "cmake" -Arguments @(
        "--build", (Join-Path $repoRoot "Native\build_x86"),
        "--config", $Configuration
    )
}

$mainExePath = Join-Path $mainPublishDir "Hotkey-Translator.exe"
$helperExePath = Join-Path $helperPublishDir "WinRtLanguagePackElevator.exe"
$hookHostX64Path = Join-Path $nativeStageX64Dir "HookHost.exe"
$hookDx9X64Path = Join-Path $nativeStageX64Dir "HookAgentDx9.dll"
$hookDx11X64Path = Join-Path $nativeStageX64Dir "HookAgentDx11.dll"
$hookVulkanX64Path = Join-Path $nativeStageX64Dir "HookAgentVulkan.dll"

Write-Step "Collecting x86 native hook outputs"
[void](Save-ArtifactIfPresent -Source (Join-Path $repoRoot "Native\HookHost\bin\x86\HookHost.exe") -Destination (Join-Path $nativeStageX86Dir "HookHost.exe") -Description "x86 HookHost" -Required)
[void](Save-ArtifactIfPresent -Source (Join-Path $repoRoot "Native\HookHost\bin\x86\HookAgentDx9.dll") -Destination (Join-Path $nativeStageX86Dir "HookAgentDx9.dll") -Description "x86 HookAgentDx9")
[void](Save-ArtifactIfPresent -Source (Join-Path $repoRoot "Native\HookHost\bin\x86\HookAgentDx11.dll") -Destination (Join-Path $nativeStageX86Dir "HookAgentDx11.dll") -Description "x86 HookAgentDx11")
[void](Save-ArtifactIfPresent -Source (Join-Path $repoRoot "Native\HookHost\bin\x86\HookAgentVulkan.dll") -Destination (Join-Path $nativeStageX86Dir "HookAgentVulkan.dll") -Description "x86 HookAgentVulkan")

$hookHostX86Path = Join-Path $nativeStageX86Dir "HookHost.exe"
$hookDx9X86Path = Join-Path $nativeStageX86Dir "HookAgentDx9.dll"
$hookDx11X86Path = Join-Path $nativeStageX86Dir "HookAgentDx11.dll"
$hookVulkanX86Path = Join-Path $nativeStageX86Dir "HookAgentVulkan.dll"

Write-Step "Validating build outputs"
Assert-PathExists -Path $mainExePath -Description "Main WPF executable"
Assert-PathExists -Path $helperExePath -Description "WinRT helper executable"
Assert-PathExists -Path $hookHostX64Path -Description "x64 HookHost"
Assert-PathExists -Path $hookHostX86Path -Description "x86 HookHost"
[void](Assert-OptionalPath -Path $hookVulkanX64Path -Description "x64 HookAgentVulkan")
[void](Assert-OptionalPath -Path $hookDx9X64Path -Description "x64 HookAgentDx9")
[void](Assert-OptionalPath -Path $hookDx11X64Path -Description "x64 HookAgentDx11")
[void](Assert-OptionalPath -Path $hookDx9X86Path -Description "x86 HookAgentDx9")
[void](Assert-OptionalPath -Path $hookDx11X86Path -Description "x86 HookAgentDx11")
[void](Assert-OptionalPath -Path $hookVulkanX86Path -Description "x86 HookAgentVulkan")

Write-Step "Preparing distribution directory"
Ensure-Directory -Path $OutputRoot
Reset-Directory -Path $distributionRoot

Write-Step "Copying published executables"
Copy-File -Source $mainExePath -Destination (Join-Path $distributionRoot "Hotkey-Translator.exe")
Copy-File -Source $helperExePath -Destination (Join-Path $distributionRoot "Tools\WinRtLanguagePackElevator\WinRtLanguagePackElevator.exe")

Write-Step "Copying required tools"
Copy-File -Source (Join-Path $repoRoot "Tools\uv\uv.exe") -Destination (Join-Path $distributionRoot "Tools\uv\uv.exe")
Copy-FilteredTree -Source (Join-Path $repoRoot "Tools\Magpie") -Destination (Join-Path $distributionRoot "Tools\Magpie") -ExcludedDirectoryNames @("cache", "logs")
Ensure-Directory -Path (Join-Path $distributionRoot "Tools\Magpie\cache")
Ensure-Directory -Path (Join-Path $distributionRoot "Tools\Magpie\logs")

Write-Step "Copying native hook binaries"
Copy-File -Source $hookHostX64Path -Destination (Join-Path $distributionRoot "Native\HookHost\bin\HookHost.exe")
if (Test-Path -LiteralPath $hookDx9X64Path) {
    Copy-File -Source $hookDx9X64Path -Destination (Join-Path $distributionRoot "Native\HookHost\bin\HookAgentDx9.dll")
}
if (Test-Path -LiteralPath $hookDx11X64Path) {
    Copy-File -Source $hookDx11X64Path -Destination (Join-Path $distributionRoot "Native\HookHost\bin\HookAgentDx11.dll")
}
if (Test-Path -LiteralPath $hookVulkanX64Path) {
    Copy-File -Source $hookVulkanX64Path -Destination (Join-Path $distributionRoot "Native\HookHost\bin\HookAgentVulkan.dll")
}

Copy-File -Source $hookHostX86Path -Destination (Join-Path $distributionRoot "Native\HookHost\bin\x86\HookHost.exe")
if (Test-Path -LiteralPath $hookDx9X86Path) {
    Copy-File -Source $hookDx9X86Path -Destination (Join-Path $distributionRoot "Native\HookHost\bin\x86\HookAgentDx9.dll")
}
if (Test-Path -LiteralPath $hookDx11X86Path) {
    Copy-File -Source $hookDx11X86Path -Destination (Join-Path $distributionRoot "Native\HookHost\bin\x86\HookAgentDx11.dll")
}
if (Test-Path -LiteralPath $hookVulkanX86Path) {
    Copy-File -Source $hookVulkanX86Path -Destination (Join-Path $distributionRoot "Native\HookHost\bin\x86\HookAgentVulkan.dll")
}

Write-Step "Copying online OCR and translation service sources"
Copy-FilteredTree -Source (Join-Path $repoRoot "OcrService") -Destination (Join-Path $distributionRoot "OcrService") -ExcludedDirectoryNames $serviceExcludedDirectories -ExcludedFilePatterns $serviceExcludedFiles
Copy-FilteredTree -Source (Join-Path $repoRoot "OcrServiceNDL") -Destination (Join-Path $distributionRoot "OcrServiceNDL") -ExcludedDirectoryNames $serviceExcludedDirectories -ExcludedFilePatterns $serviceExcludedFiles
Copy-FilteredTree -Source (Join-Path $repoRoot "OcrServiceVL") -Destination (Join-Path $distributionRoot "OcrServiceVL") -ExcludedDirectoryNames $serviceExcludedDirectories -ExcludedFilePatterns $serviceExcludedFiles
Copy-FilteredTree -Source (Join-Path $repoRoot "OcrServiceVisionLlm") -Destination (Join-Path $distributionRoot "OcrServiceVisionLlm") -ExcludedDirectoryNames $serviceExcludedDirectories -ExcludedFilePatterns $serviceExcludedFiles

# WHY: Online distribution intentionally excludes GGUF payloads while keeping llama.cpp runtime layout stable.
Copy-FilteredTree -Source (Join-Path $repoRoot "TranslationServiceLlama") -Destination (Join-Path $distributionRoot "TranslationServiceLlama") -ExcludedDirectoryNames $serviceExcludedDirectories -ExcludedFilePatterns $serviceExcludedFiles -ExcludedRelativeDirectoryPatterns @("LlamaCpp\Models")
Ensure-Directory -Path (Join-Path $distributionRoot "TranslationServiceLlama\LlamaCpp\Models")

Write-Step "Writing distribution manifest"
$notesPath = Join-Path $distributionRoot "DIST-NOTES.txt"
@(
    "Hotkey-Translator online distribution",
    "",
    "Included:",
    "- Single-file WPF application",
    "- WinRtLanguagePackElevator helper",
    "- x64/x86 HookHost and DirectX hook agents",
    "- uv runtime bootstrapper",
    "- Magpie runtime files",
    "- OCR/translation Python service sources",
    "- llama.cpp runtime binaries without GGUF model payloads",
    "",
    "Not included:",
    "- TranslationService directory",
    "- Python .venv directories",
    "- GGUF and mmproj model files",
    "- Paddle managed model payloads",
    "",
    "Hook DLL note:",
    "- Hook agent DLLs can be quarantined by local antivirus during build or packaging.",
    "- Missing hook artifacts are listed below when that happens.",
    "",
    "First-run behavior:",
    "- Python hosts create their local .venv via uv sync.",
    "- Default llama/VisionLLM models download from model_manifest.json when selected.",
    "- Paddle/PaddleVL managed models download through their runtime on demand."
) | Set-Content -Path $notesPath -Encoding UTF8

if ($script:missingHookArtifacts.Count -gt 0) {
    Add-Content -Path $notesPath -Encoding UTF8 -Value ""
    Add-Content -Path $notesPath -Encoding UTF8 -Value "Missing hook artifacts:"
    foreach ($missingArtifact in $script:missingHookArtifacts) {
        Add-Content -Path $notesPath -Encoding UTF8 -Value "- $missingArtifact"
    }
}

Write-Step "Distribution completed"
Write-Host "Output: $distributionRoot" -ForegroundColor Green
