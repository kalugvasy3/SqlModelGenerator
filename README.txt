SqlModelGenerator
=================

Purpose
-------
.NET 8 console generator that reads SQL Server metadata and writes C# code files for:
- tables (POCO classes)
- views (POCO classes)
- stored procedures (wrapper methods + result classes when first result set can be described)
- DbContext

What to change first
--------------------
1. Open Program.cs
2. Replace ConnectionString with your real SQL Server connection string
3. Run: dotnet restore
4. Run: dotnet run

Output
------
Generated folder under the app output directory.

Important notes
---------------
- The generator itself only needs Microsoft.Data.SqlClient.
- The generated model files use EF Core APIs, so your target application should reference:
  - Microsoft.EntityFrameworkCore
  - Microsoft.EntityFrameworkCore.SqlServer
- Views are generated as keyless entities.
- Procedures use sp_describe_first_result_set. If SQL Server cannot describe a procedure result shape,
  the method is still generated, but as a non-query method returning Task<int>.
- Multi-result-set procedures are not handled in this first version.
