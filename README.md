# Solution Documentor

A powerful XrmToolBox plugin for visualizing and documenting Microsoft Dataverse solutions.

## Features

- 📊 **Entity Relationship Diagrams (ERD)**: Generate interactive ERDs from your Dataverse solutions
- 📝 **Solution Documentation**: Generate comprehensive markdown documentation including:
  - Tables & Columns
  - Option Sets
  - Security Roles
  - Power Automate Flows
- 🔍 **Option Sets Browser**: Browse and explore option sets in your solution
- 🔐 **Security Roles Viewer**: Visualize security role permissions
- 🌊 **Flow Explorer**: Document and view Power Automate flows

## Installation

### From XrmToolBox Plugin Store
1. Open XrmToolBox
2. Go to **Tool Library** or **Plugins Manager**
3. Search for "Solution Documentor"
4. Click **Install**

### Manual Installation
1. Download the latest release from the [Releases](../../releases) page
2. Extract `XrmDataversePlugin.dll` to:
   ```
   %APPDATA%\MscrmTools\XrmToolBox\Plugins\
   ```
3. Restart XrmToolBox

## Usage

1. Launch XrmToolBox and connect to your Dataverse environment
2. Open **Solution Documentor** from the tools list
3. Select a solution from the dropdown
4. Choose what you want to generate:
   - **ERD Tab**: Select entities and generate visual diagrams
   - **Option Sets Tab**: Browse option sets
   - **Security Roles Tab**: View role permissions
   - **Flows Tab**: Explore Power Automate flows
   - **📄 Docs Tab**: Generate full solution documentation

### Generating Documentation
1. Go to the **📄 Docs** tab
2. Select what to include (Tables, Option Sets, Roles, Flows)
3. Click **Generate Documentation**
4. Click **💾 Save .md** to save the markdown file

## Requirements

- XrmToolBox (latest version recommended)
- Microsoft Dataverse / Dynamics 365 CE environment
- .NET Framework 4.8+

## Built With

- .NET Framework 4.8
- Microsoft WebView2
- XrmToolBox SDK
- Microsoft.Xrm.Sdk

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Author

**Nate Halliwell**

Powered by your friends at **Pragmatic Works**

## Acknowledgments

- Built for the XrmToolBox community
- Inspired by the need for better Dataverse solution documentation

## Support

If you encounter any issues or have suggestions, please [open an issue](../../issues).

---

*Made with ❤️ for the Dynamics 365 community*
