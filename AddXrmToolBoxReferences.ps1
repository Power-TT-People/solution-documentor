# Script to add XrmToolBox references to the project file
$projectFile = "SolutionDocumentor.csproj"

Write-Host "Reading project file..." -ForegroundColor Cyan
$content = Get-Content $projectFile -Raw

# Check if references already exist
if ($content -match 'XrmToolBox') {
    Write-Host "XrmToolBox references already exist in the project file!" -ForegroundColor Yellow
    exit 0
}

# References to add
$xrmToolBoxRef = @"
    <Reference Include="XrmToolBox">
      <HintPath>packages\XrmToolBoxPackage.1.2025.10.74\lib\net48\XrmToolBox.exe</HintPath>
      <Private>False</Private>
    </Reference>
    <Reference Include="McTools.Xrm.Connection">
      <HintPath>packages\MscrmTools.Xrm.Connection.1.2025.7.63\lib\net462\McTools.Xrm.Connection.dll</HintPath>
      <Private>False</Private>
    </Reference>
"@

# Find the exact location to insert (after the last Reference tag, before </ItemGroup>)
$searchText = @"
    <Reference Include="WeifenLuo.WinFormsUI.Docking.ThemeVS2015, Version=3.0.6.0, Culture=neutral, PublicKeyToken=5cded1a1a0a7b481, processorArchitecture=MSIL">
      <HintPath>packages\DockPanelSuite.ThemeVS2015.3.0.6\lib\net40\WeifenLuo.WinFormsUI.Docking.ThemeVS2015.dll</HintPath>
    </Reference>
  </ItemGroup>
"@

$replaceText = @"
    <Reference Include="WeifenLuo.WinFormsUI.Docking.ThemeVS2015, Version=3.0.6.0, Culture=neutral, PublicKeyToken=5cded1a1a0a7b481, processorArchitecture=MSIL">
      <HintPath>packages\DockPanelSuite.ThemeVS2015.3.0.6\lib\net40\WeifenLuo.WinFormsUI.Docking.ThemeVS2015.dll</HintPath>
    </Reference>
$xrmToolBoxRef
  </ItemGroup>
"@

if ($content.Contains($searchText)) {
    Write-Host "Adding XrmToolBox references..." -ForegroundColor Green
    $newContent = $content.Replace($searchText, $replaceText)
    $newContent | Set-Content $projectFile -NoNewline
    Write-Host "Successfully added XrmToolBox references to project file!" -ForegroundColor Green
} else {
    Write-Host "Could not find exact insertion point. The project file may have been modified." -ForegroundColor Red
    Write-Host "Please add references manually using Visual Studio." -ForegroundColor Yellow
}

Write-Host "`nDone! Reopen the solution in Visual Studio and rebuild." -ForegroundColor Cyan

