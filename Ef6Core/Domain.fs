namespace CommonClassLibrary

open System

[<RequireQualifiedAccess>]
type SqlCatalogObjectKind =
    | Table
    | View
    | Column
    | Function
    | Procedure
    | Trigger

/// Catalog identities retain the exact spelling exposed by SQL Server.
type SqlCatalogObject =
    { Kind: SqlCatalogObjectKind
      Schema: string
      Name: string
      ParentName: string
      DataType: string }

module SqlMetadataNotice =
    [<Literal>]
    let RelationshipVisibility = "Only relationships visible to this database connection are included; hidden or remote relationships cannot be inferred."

    let IsInformation(text: string) =
        String.Equals(text, RelationshipVisibility, StringComparison.Ordinal)

    let ReadWarnings(warnings: string array) =
        if isNull warnings then [||]
        else warnings |> Array.filter (IsInformation >> not) |> Array.distinct

    let Information(warnings: string array) =
        if isNull warnings then [||]
        else warnings |> Array.filter IsInformation |> Array.distinct

type SqlCatalogSnapshot =
    { ProviderId: string
      UsesAccessSyntax: bool
      DatabaseName: string
      ServerVersion: string
      Objects: SqlCatalogObject array
      Keywords: string array
      Warnings: string array
      Elapsed: TimeSpan }
    member this.ReadWarnings = SqlMetadataNotice.ReadWarnings this.Warnings
    member this.Information = SqlMetadataNotice.Information this.Warnings

type SqlRelationshipTable = { Catalog: string; Schema: string; Name: string }
type SqlRelationshipColumn = { ChildColumn: string; ParentColumn: string; Ordinal: int }
type SqlForeignKeyRelationship =
    { Name: string
      Child: SqlRelationshipTable
      Parent: SqlRelationshipTable
      Columns: SqlRelationshipColumn array }

type SqlRelationshipCatalog =
    { Relationships: SqlForeignKeyRelationship array
      Warnings: string array
      DialectId: string option
      Complete: bool }
