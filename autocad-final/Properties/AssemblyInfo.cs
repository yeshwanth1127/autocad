using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.Runtime;
using autocad_final.Commands;
using autocad_final;

[assembly: CommandClass(typeof(DefineBuildingAreaCommand))]
[assembly: CommandClass(typeof(PolygonAreaCommand))]
[assembly: CommandClass(typeof(LoopAreaCommand))]
[assembly: CommandClass(typeof(PointsAreaCommand))]
[assembly: CommandClass(typeof(ZoneAreaCommand))]
[assembly: CommandClass(typeof(RouteMainPipeCommand))]
[assembly: CommandClass(typeof(ApplySprinklersCommand))]
[assembly: CommandClass(typeof(PlaceSprinklersCommand))]
[assembly: CommandClass(typeof(CheckSprinklersAndFixCommand))]
[assembly: CommandClass(typeof(FixOnSlantCommand))]
[assembly: CommandClass(typeof(AttachBranchesCommand))]
[assembly: CommandClass(typeof(DeleteAllOnLayerCommand))]
[assembly: CommandClass(typeof(RebuildFromTrunkCommand))]
[assembly: CommandClass(typeof(RedesignFromTrunkCommand))]
[assembly: CommandClass(typeof(SprinklerDesignCommand))]
[assembly: CommandClass(typeof(SelectShaftPointCommand))]
[assembly: CommandClass(typeof(ZoneTestCommand))]
[assembly: CommandClass(typeof(FixZoneBoundaryCommand))]
[assembly: CommandClass(typeof(ZoneCreation1Command))]
[assembly: CommandClass(typeof(ZoneCreation2Command))]
[assembly: CommandClass(typeof(FixZonesCommand))]
[assembly: ExtensionApplication(typeof(SprinklerPaletteExtensionApplication))]

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("autocad-final")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("autocad-final")]
[assembly: AssemblyCopyright("Copyright ©  2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("ea04f805-c87e-4c73-afff-9b94f1230c30")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
[assembly: AssemblyVersion("1.0.0.8")]
[assembly: AssemblyFileVersion("1.0.0.8")]
