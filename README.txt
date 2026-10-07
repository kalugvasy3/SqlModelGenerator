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
   Optional environment variables: SQLMODELGENERATOR_CONNECTION_STRING and
   SQLMODELGENERATOR_OUTPUT override the defaults in Program.cs.

Output
------
Generated folder under the app output directory.

Important notes
---------------
- The generator itself only needs Microsoft.Data.SqlClient.
- The generated code targets .NET Framework 4.8, C# 7.3, and Entity Framework 6.
  Install the EntityFramework 6.5.x NuGet package in the consuming application.
  No Microsoft.EntityFrameworkCore package is needed.
- The generated AppDbContext uses the connection string named AppDbContext by default.
  Alternatively, pass a connection string or connection-string name to its constructor.
- EF6 cannot map a table without a primary key. Such a table gets a POCO but no DbSet;
  check Report.txt for the list. Views have Get<ViewName>ViewAsync read methods.
- Procedure methods use System.Data.SqlClient and the context's SQL connection. They
  materialize the first result set by ordinal, so unusual SQL column names also work.
  OUTPUT parameters are sent to SQL Server but are not returned by the current API.
- Procedures use sp_describe_first_result_set. If SQL Server cannot describe a procedure result shape,
  the method is still generated, but as a non-query method returning Task<int>.
- Multi-result-set procedures are not handled in this first version.
- This version writes directly to its output directory. Generate into a separate folder
  when comparing with an existing model; review files before replacing application code.
