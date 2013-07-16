namespace FSharp.MongoDB.Driver

open System.Collections
open System.Collections.Generic

open MongoDB.Bson

open MongoDB.Driver.Core
open MongoDB.Driver.Core.Protocol

[<AutoOpen>]
module Fluent =

    type Internals = {
        Agent : MongoAgent
        Database : string
        Collection : string
    }

    type QueryOptions = {
        Comment : string option
        Hint : BsonDocument option
        MaxScan : int option
        Max : obj option
        Min : obj option
        Snapshot : bool option
    }

    let defaultQueryOptions = {
        Comment = None
        Hint = None
        MaxScan = None
        Max = None
        Min = None
        Snapshot = None
    }

    let makeQueryDoc query sort (options : QueryOptions) =
        let addElem name value (doc : BsonDocument) =
            match value with
            | Some x -> doc.Add(name, BsonValue.Create(x))
            | None -> doc

        match query with
        | Some x ->
            BsonDocument("$query", x)
            |> addElem "$orderby" sort
            |> addElem "$comment" options.Comment
            |> addElem "$hint" options.Hint
            |> addElem "$maxScan" options.MaxScan
            |> addElem "$max" options.Max
            |> addElem "$min" options.Min
            |> addElem "$snapshot" options.Snapshot
        | None -> failwith "unset query"

    type Scope = {
        Internals : Internals option

        Query : BsonDocument option
        Project : BsonDocument option
        Sort : BsonDocument option

        Limit : int
        Skip : int

        QueryOptions : QueryOptions
    } with
        member x.Get (?flags0) =
            let flags = defaultArg flags0 QueryFlags.None

            match x.Internals with
            | Some  { Agent = agent; Database = db; Collection = clctn } ->
                let query = makeQueryDoc x.Query x.Sort x.QueryOptions

                let project =
                    match x.Project with
                    | Some x -> x
                    | None -> null

                let limit = x.Limit
                let skip = x.Skip

                let settings = MongoOperationSettings.Defaults.querySettings

                async {
                    let! cursor = agent.Find db clctn query project limit skip flags settings
                    return cursor.GetEnumerator()
                }

            | None -> failwith "unset collection"

        interface IEnumerable<BsonDocument> with
            member x.GetEnumerator() = x.Get() |> Async.RunSynchronously

        interface IEnumerable with
            override x.GetEnumerator() = (x :> IEnumerable<BsonDocument>).GetEnumerator() :> IEnumerator

    let defaultScope = {
        Internals = None

        Query = None
        Project = None
        Sort = None

        Limit = 0
        Skip = 0

        QueryOptions = defaultQueryOptions
    }

    [<RequireQualifiedAccess>]
    module Query =

        let find query scope =
            { scope with Query = Some query }

        let fields project scope =
            { scope with Project = Some project }

        let sort order scope =
            { scope with Sort = Some order }

        let limit n scope =
            { scope with Limit = n }

        let skip n scope =
            { scope with Skip = n }

        let withQueryOptions options scope =
            { scope with QueryOptions = options }

type MongoCollection(agent : MongoAgent, db, clctn) =

    member __.Drop () = agent.DropCollection db clctn

    member __.BulkInsert docs = agent.BulkInsert db clctn docs

    member __.Insert doc = agent.Insert db clctn doc

    member x.Find (?query0 : BsonDocument) =
        let query = defaultArg query0 <| BsonDocument()

        { defaultScope with Internals = Some { Agent = agent; Database = db; Collection = clctn }
                            Query = Some query }

    member __.Update query update = agent.Update db clctn query update

    member __.Remove query = agent.Remove db clctn query
