namespace CommonClassLibrary

// Extracted from AnotherEdit CommonClassLibrary/PocoGeneratorTypes.fs (2026-11-18).
// This standalone port retains the EF6 generation/source-compatibility path.

open System
open System.Data

[<RequireQualifiedAccess>]
type PocoTarget =
    | Ef6
    // Detection sentinel only: the standalone generator rejects EF Core sources.
    | EfCore

[<RequireQualifiedAccess>]
type PocoNaming =
    | AsInDatabase
    | Singular
    | Plural

type PocoColumn =
    { Name: string
      ClrType: Type
      StoreType: string
      Nullable: bool option
      Size: int option
      Precision: int option
      Scale: int option
      AutoNumber: bool
      Computed: bool }

type PocoParameter =
    { Column: PocoColumn
      Direction: ParameterDirection
      Ordinal: int }

[<RequireQualifiedAccess>]
type PocoRoutineResult =
    | NoRows
    | Rows of PocoColumn array
    | Unknown of string

type PocoObject =
    { Item: SqlCatalogObject
      Columns: PocoColumn array
      PrimaryKey: string array
      UniqueKeys: string array array
      Parameters: PocoParameter array
      ParametersKnown: bool
      RoutineResult: PocoRoutineResult
      Warnings: string array }

type PocoRelationshipDetails =
    { Name: string
      Child: SqlRelationshipTable
      DeleteAction: string
      UpdateAction: string
      Disabled: bool
      NotTrusted: bool }

type PocoSchemaSnapshot =
    { ProviderId: string
      UsesAccessSyntax: bool
      Objects: PocoObject array
      Relationships: SqlForeignKeyRelationship array
      RelationshipDetails: PocoRelationshipDetails array
      Warnings: string array }

type PocoObjectNames =
    { ClassName: string
      SetName: string
      Properties: Map<string, string> }

type PocoOptions =
    { Namespace: string
      ContextName: string
      Target: PocoTarget
      ClassNaming: PocoNaming
      SetNaming: PocoNaming
      NullableReferenceTypes: bool
      Overrides: Map<SqlCatalogObjectKind * string * string, PocoObjectNames> }

type PocoGeneratedFile =
    { FileName: string
      Text: string }

type PocoGeneration =
    { Files: PocoGeneratedFile array
      Warnings: string array }
