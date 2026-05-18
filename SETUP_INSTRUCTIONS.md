# Complete Setup Instructions for XrmToolBox Plugin

## ✅ Steps Already Completed:
1. ✓ Installed XrmToolBoxPackage (version 1.2025.10.74)
2. ✓ Installed MscrmTools.Xrm.Connection (version 1.2025.7.63)
3. ✓ Updated packages.config with new packages
4. ✓ Created .nuspec file for packaging

## 📋 What You Need to Do Now:

### Step 1: Add Assembly References
**IN VISUAL STUDIO:**
1. Right-click on your project "SolutionDocumentor" in Solution Explorer
2. Select "Add" → "Reference..."
3. Click "Browse..." button
4. Navigate to: `C:\Users\halli\source\repos\SolutionDocumentor\packages\XrmToolBoxPackage.1.2025.10.74\lib\net48\`
5. Select these TWO files:
   - `XrmToolBox.exe`
   - (Then browse to) `C:\Users\halli\source\repos\SolutionDocumentor\packages\MscrmTools.Xrm.Connection.1.2025.7.63\lib\net462\McTools.Xrm.Connection.dll`
6. Click "Add"
7. **IMPORTANT:** In Solution Explorer, expand "References", find "XrmToolBox" and "McTools.Xrm.Connection"
8. Right-click each one → Properties → Set "Copy Local" to **False**

### Step 2: Rebuild the Project
1. Build → Rebuild Solution (or press Ctrl+Shift+B)
2. Verify there are no errors

### Step 3: Create NuGet Package
Run this command in PowerShell:
```powershell
cd "C:\Users\halli\source\repos\SolutionDocumentor\"
nuget pack SolutionDocumentor.nuspec -Properties Configuration=Release
```

This will create a `.nupkg` file with your DLL in the Plugins folder structure!

---

## 🎯 Alternative: Automated Script
If you prefer, you can:
1. **Close Visual Studio completely**
2. Run: `.\AddXrmToolBoxReferences.ps1`
3. Reopen Visual Studio
4. Build the solution

The script will automatically add the references to your .csproj file.

---

## 📦 Your NuGet Package Structure
After packing, your package will have this structure:
```
SolutionDocumentor.1.0.0.nupkg
└── lib
    └── net48
        └── Plugins
            ├── SolutionDocumentor.dll
            ├── SolutionDocumentor.pdb
            ├── Microsoft.Extensions.AI.dll
            ├── Microsoft.Extensions.AI.Abstractions.dll
            └── ScintillaNET.dll
```

This matches the XrmToolBox plugin convention where plugins are loaded from the Plugins subfolder!
