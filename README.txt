SqlModelGenerator - SQL Server to Entity Framework 6
===================================================

One C# console project
---------------------
Open SqlModelGenerator.sln and run SqlModelGenerator. All generator code is C#
inside this project. No companion project, F# runtime, or AnotherEdit installation
is required. The generated model is also C#.

The console targets .NET 8. Build with .NET SDK 8 or newer:

    dotnet build SqlModelGenerator.sln
    dotnet run --project SqlModelGenerator.csproj

Input settings
--------------
Edit the INPUT PARAMETERS block at the beginning of Program.cs. Command-line
arguments and environment variables do not override these values. The existing
connection, output folder, namespace, and context settings have been retained.

- ConnectionString: SQL Server connection used only to read schema metadata.
  The generator does not execute procedures or change database data.
- OutputFolder: a dedicated folder for generated files. On the first run of
  this generator, choose an empty folder if legacy/untracked model files exist.
- RootNamespace / ContextName: the generated namespace and DbContext name.
- MetadataTimeoutSeconds / SchemaFilter / Include...: metadata timeout, exact
  schema filter (empty means all visible user schemas), and object categories.
  Foreign keys are always inspected for selected tables.
- ClassNaming / SetNaming: database spelling, singular, or plural names.
- NullableReferenceTypes: false by default for .NET Framework 4.8 / C# 7.3.
  Nullable value types such as int? and DateTime? remain supported.
- ExistingModelPath: optional existing EF6 model folder or project. Compatible
  generation retains existing class/DbSet/property/navigation names, interfaces,
  constructors, defaults, methods, and procedure contracts. The source model is
  read-only and must be separate from the output folder.
- DestinationProjectPath: optional consumer .csproj to determine supported C#
  nullable syntax. The consumer project is not changed.
- OverwriteGeneratedFiles / RemoveObsoleteGeneratedFiles: update/retire only
  owned files that have not been manually edited.
- NameOverrides: optional explicit names for objects and columns; see Program.cs.

Generated EF6 model
-------------------
- Table/view POCO classes with exact database column mappings and CLR types.
- DbContext fluent mappings: schema/table, ordered composite primary keys,
  identity, computed/rowversion, lengths/MAX, precision/scale, and nullability.
- Declared foreign keys, parent navigation properties, inverse collections,
  required/optional relationships, composite FK, and cascade-delete policies.
- Shared-primary-key one-to-one mappings. EF6 restrictions on alternate keys,
  disabled, unsupported, or hidden relationships are reported in Report.txt.
  A column name resembling a FK does not invent a relationship.
- Views/keyless tables get POCO and Query...(): DbRawSqlQuery<T>. No fake primary
  key is created to force a DbSet mapping.
- Stored procedures: Arguments/Outputs/Row DTO, command factories, sync/async
  wrappers, all rows and result sets, OUTPUT and return values after reader
  disposal. The DataSet path retains empty result sets and duplicate SQL aliases.
- Report.txt with object/relationship counts and metadata warnings.

The EF6 generator does not include EF Core, migration conversion, TypeScript,
or UI code. Database functions do not gain new wrappers; existing methods can
be retained as part of an existing-model contract.

Using the generated model
-------------------------
The default output is compatible with .NET Framework 4.8, C# 7.3, and EF6.
The consumer needs EntityFramework 6.x, System.Configuration,
System.ComponentModel.DataAnnotations, and System.Data.SqlClient. The generator
itself uses Microsoft.Data.SqlClient for metadata and Roslyn for C# source edits.

The context disables automatic database initialization. It has constructors for
a named application connection and for a supplied DbConnection. Configure the
named connection in App.config/Web.config using ContextName and
providerName="System.Data.SqlClient". The real connection string belongs to the
application; it is not copied into generated source files.

Updating output / cancellation
------------------------------
The .anotheredit-poco.json ownership manifest remains compatible with previously
generated output. Keep it with the model. Publishing is atomic with rollback;
manually edited files and untracked legacy C# files are preserved. There is no
recursive clearing of the output directory.

Ctrl+C cancels metadata reads and generation. Connections, commands, readers,
and cancellation subscriptions are released. No background polling or timers
are used. Once atomic publication begins, it completes or rolls back as a unit.

Validation
----------
The C# implementation is compared with the previously tested generator for exact
generated source and warning parity. Checks cover Missouri-style nonconventional
FK, composite keys, required/optional relationships, model compatibility,
compilation with real EF6 under C# 7.3/.NET 4.8, procedure runtime behavior, and
safe file publication. Tests do not execute application procedures or read data.
