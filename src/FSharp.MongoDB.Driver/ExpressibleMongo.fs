(* Copyright (c) 2013 MongoDB, Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *)

namespace FSharp.MongoDB.Driver

open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Quotations.Patterns
open Microsoft.FSharp.Quotations.DerivedPatterns
open Microsoft.FSharp.Reflection

open MongoDB.Bson

open MongoDB.Driver.Core

open FSharp.MongoDB.Driver.Quotations

module Expression =

    let private queryParser = Query.parser

    let private updateParser = Update.parser

    /// <summary>
    /// An input into a query operation.
    /// This type is used to support the MongoDB query syntax.
    /// </summary>
    type IMongoQueryable<'a> =
        inherit seq<'a>

    /// <summary>
    /// An input into a update operation.
    /// This type is used to support the MongoDB update syntax.
    /// </summary>
    type IMongoUpdatable<'a> =
        inherit seq<'a>

    /// <summary>
    /// An input into a update operation.
    /// This type is used to support the MongoDB <c>$each</c> update syntax.
    /// </summary>
    type IMongoEachUpdatable<'a> =
        inherit IMongoUpdatable<'a>

    /// <summary>
    /// A result of a query or update operation.
    /// This type is used to return the expressions as <c>BsonDocument</c>s,
    /// rather than actually executing against the database.
    /// </summary>
    type IMongoDeferrable<'a> =
        inherit seq<'a>

    // TODO: add find and modify case

    [<RequireQualifiedAccess>]
    /// Represents different possible results from transforming a quotation.
    type private TransformResult =
       | For of Expr // expr, since `cont` is unnecessary here
       | Query of (Var -> Expr -> BsonElement option) * (Var * Expr) * Expr // (fun * args * cont)
       | Update of (Var -> string -> Expr -> BsonElement option) * (Var * string * Expr) * Expr // (fun * args * cont)
       | Pass of Expr // cont

    [<RequireQualifiedAccess>]
    /// Represents different possible results from traversing a quotation.
    type private TraverseResult =
       | For of Expr // expr
       | Query of (Var -> Expr -> BsonElement option) * (Var * Expr) // (fun * args)
       | Update of (Var -> string -> Expr -> BsonElement option) * (Var * string * Expr) // (fun * args)

    /// Represents different operation cases from using the `defer` keyword.
    type MongoDeferredOperation =
       | Query of BsonDocument
       | Update of BsonDocument * BsonDocument

    [<RequireQualifiedAccess>]
    /// Represents different cases from the overall `mongo { ... }` expression.
    type MongoOperationResult<'a> =
       | Query of Scope<'a>
       | Update of WriteConcernResult
       | Deferred of MongoDeferredOperation

    /// <summary>
    /// The type used to support the MongoDB query and update syntax from F#.
    /// </summary>
    type MongoBuilder() =

        let mkCall op args =
            match <@ ignore %op @> with
            | SpecificCall <@ ignore @> (_, _, [ Lambdas (_, Call (target, info, _)) ]) ->
                match target with
                | Some x -> Expr.Call(x, info, args)
                | None -> Expr.Call(info, args)
            | _ -> failwith "unable to acquire methodinfo"

        // Ignores inputs and returns partially applied parameter
        //      intended usage: fakeParser elem
        let fakeParser (res : BsonElement) (var : Var) (field : string) (expr : Expr) = Some res

        let transform (x : MongoBuilder) expr =
            let (|PushEach|_|) expr =
                match expr with
                | SpecificCall <@ x.PushEach @> (_, _, [ cont; Lambda (_, body); List (values, _) ]) ->
                    let (var, field, body) =
                        match body with
                        | CallDynamic (var, field) -> (var, field, body)
                        | GetProperty (var, field) -> (var, field, body)
                        | _ -> failwithf "unrecognized expression\n%A" body

                    let bsonValues =
                        values
                        |> List.map (fun x ->
                            try
                                BsonValue.Create x
                            with
                               | :? System.ArgumentException -> x.ToBsonDocument(x.GetType()) :> BsonValue)

                    let elem =  BsonElement("$push", BsonDocument(field, BsonDocument("$each", BsonArray(bsonValues))))
                    let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                    Some res

                | _ -> None

            let rec (|PushEachWithModifiers|_|) expr =
                match expr with
                | SpecificCall <@ x.Slice @> (_, _, [ PushEach (res); Int32 (value) ])
                | SpecificCall <@ x.Slice @> (_, _, [ PushEachWithModifiers (res); Int32 (value) ]) ->
                    let maybeElem =
                        match res with
                        | TransformResult.Update (f, args, cont) -> args |||> f
                        | _ -> None

                    match maybeElem with
                    | Some elem ->
                        // overwrite, since using last assigned value
                        elem.Value.[0].AsBsonDocument.Set("$slice", BsonInt32(value)) |> ignore // e.g. { $push: { <field>: { $each ... } }
                        Some res
                    | None -> None

                | SpecificCall <@ x.SortListAscending @> (_, _, [ PushEach (res); Lambda (_, GetField (_, array)); Lambda (_, GetField (_, field)) ])
                | SpecificCall <@ x.SortListAscending @> (_, _, [ PushEachWithModifiers (res); Lambda (_, GetField (_, array)); Lambda (_, GetField (_, field)) ]) ->
                    let maybeElem =
                        match res with
                        | TransformResult.Update (f, args, cont) -> args |||> f
                        | _ -> None

                    match maybeElem with
                    | Some elem ->
                        // overwrite, since using last assigned value
                        elem.Value.[0].AsBsonDocument.Set("$sort", BsonDocument(field, BsonInt32(1))) |> ignore // e.g. { $push: { <field>: { $each ... } }
                        Some res
                    | None -> None

                | SpecificCall <@ x.SortListDescending @> (_, _, [ PushEach (res); Lambda (_, GetField (_, array)); Lambda (_, GetField (_, field)) ])
                | SpecificCall <@ x.SortListDescending @> (_, _, [ PushEachWithModifiers (res); Lambda (_, GetField (_, array)); Lambda (_, GetField (_, field)) ]) ->
                    let maybeElem =
                        match res with
                        | TransformResult.Update (f, args, cont) -> args |||> f
                        | _ -> None

                    match maybeElem with
                    | Some elem ->
                        // overwrite, since using last assigned value
                        elem.Value.[0].AsBsonDocument.Set("$sort", BsonDocument(field, BsonInt32(-1))) |> ignore // e.g. { $push: { <field>: { $each ... } }
                        Some res
                    | None -> None

                | SpecificCall <@ x.ThenSortListAscending @> (_, _, [ PushEachWithModifiers (res); Lambda (_, GetField (_, array)); Lambda (_, GetField (_, field)) ]) ->
                    let maybeElem =
                        match res with
                        | TransformResult.Update (f, args, cont) -> args |||> f
                        | _ -> failwith "expected update transform result"

                    match maybeElem with
                    | Some elem ->
                        // append to $sort document
                        let sort = elem.Value.[0].AsBsonDocument.GetValue("$sort").AsBsonDocument
                        // overwrite, since using last assigned value
                        sort.Set(field, BsonInt32(1)) |> ignore
                        Some res
                    | None -> None

                | SpecificCall <@ x.ThenSortListDescending @> (_, _, [ PushEachWithModifiers (res); Lambda (_, GetField (_, array)); Lambda (_, GetField (_, field)) ]) ->
                    let maybeElem =
                        match res with
                        | TransformResult.Update (f, args, cont) -> args |||> f
                        | _ -> None

                    match maybeElem with
                    | Some elem ->
                        // append to $sort document
                        let sort = elem.Value.[0].AsBsonDocument.GetValue("$sort").AsBsonDocument
                        // overwrite, since using last assigned value
                        sort.Set(field, BsonInt32(-1)) |> ignore
                        Some res
                    | None -> None

                | _ -> None

            match expr with
            | SpecificCall <@ x.For @> (_, _, [ SpecificCall <@ x.Source @> (_, _, [ value ]); _ ]) ->
                let res = TransformResult.For (value)
                Some res

            // Query operations
            | SpecificCall <@ x.Where @> (_, _, [ cont; Lambda (var, body) ]) ->
                let res = TransformResult.Query (queryParser, (var, body), cont)
                Some res

            // Update operations
            | SpecificCall <@ x.Update @> (_, _, [ cont ]) ->
                let res = TransformResult.Pass cont
                Some res

            | SpecificCall <@ x.Inc @> (_, _, [ cont; Lambda (_, body); Int32 (value) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, Expr.Coerce(body, typeof<int>))
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let call = mkCall <@ (+) @> [ body; Expr.Value value ]
                let res = TransformResult.Update (updateParser, (var, field, call), cont)
                Some res

            | SpecificCall <@ x.Dec @> (_, _, [ cont; Lambda (_, body); Int32 (value) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, Expr.Coerce(body, typeof<int>))
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let call = mkCall <@ (-) @> [ body; Expr.Value value ]
                let res = TransformResult.Update (updateParser, (var, field, call), cont)
                Some res

            | SpecificCall <@ x.Set @> (_, _, [ cont; Lambda (_, body); ValueOrList (value, _) ]) ->
                match body with
                | GetField (var, field) ->
                    let res = TransformResult.Update (updateParser, (var, field, Expr.Value value), cont)
                    Some res
                | _ -> failwithf "unrecognized expression\n%A" body

            | SpecificCall <@ x.Unset @> (_, _, [ cont; Lambda (_, body) ]) ->
                match body with
                | GetField (var, field) ->
                    let cases =
                        FSharpType.GetUnionCases(typeof<option<_>>)
                        |> Seq.map (fun x -> (x.Name, x))
                        |> dict
                    let res = TransformResult.Update (updateParser, (var, field, Expr.NewUnionCase(cases.["None"], []) ), cont)
                    Some res
                | _ -> failwithf "unrecognized expression\n%A" body

            | SpecificCall <@ x.AddToSet @> (_, _, [ cont; Lambda (_, body); ValueOrList (value, _) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let elem = BsonElement("$addToSet", BsonDocument(field, BsonValue.Create value))
                let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                Some res

            | SpecificCall <@ x.PopLeft @> (_, _, [ cont; Lambda (_, body) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let elem = BsonElement("$pop", BsonDocument(field, BsonInt32(-1)))
                let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                Some res

            | SpecificCall <@ x.PopRight @> (_, _, [ cont; Lambda (_, body) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let elem = BsonElement("$pop", BsonDocument(field, BsonInt32(1)))
                let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                Some res

            | SpecificCall <@ x.Pull @> (_, _, [ cont; Lambda (_, body); Lambda (lambdaVar, lambdaExpr) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                match queryParser lambdaVar lambdaExpr with
                | Some lambdaElem ->
                    let elem = BsonElement("$pull", BsonDocument(field, BsonDocument(lambdaElem)))
                    let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                    Some res

                | None -> None

            | SpecificCall <@ x.PullAll @> (_, _, [ cont; Lambda (_, body); ValueOrList (value, _) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let elem = BsonElement("$pullAll", BsonDocument(field, BsonValue.Create value))
                let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                Some res

            | SpecificCall <@ x.Push @> (_, _, [ cont; Lambda (_, body); ValueOrList (value, _) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let elem = BsonElement("$push", BsonDocument(field, BsonValue.Create value))
                let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                Some res

            | SpecificCall <@ x.AddToSetEach @> (_, _, [ cont; Lambda (_, body); List (values, _) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let elem =  BsonElement("$addToSet", BsonDocument(field, BsonDocument("$each", BsonArray(values))))
                let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                Some res

            | PushEach (res) -> Some res

            | PushEachWithModifiers (res) -> Some res

            | SpecificCall <@ x.BitAnd @> (_, _, [ cont; Lambda (_, body); Int32 (value) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let elem = BsonElement("$bit", BsonDocument(field, BsonDocument("and", BsonInt32(value))))
                let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                Some res

            | SpecificCall <@ x.BitOr @> (_, _, [ cont; Lambda (_, body); Int32 (value) ]) ->
                let (var, field, body) =
                    match body with
                    | CallDynamic (var, field) -> (var, field, body)
                    | GetProperty (var, field) -> (var, field, body)
                    | _ -> failwithf "unrecognized expression\n%A" body

                let elem = BsonElement("$bit", BsonDocument(field, BsonDocument("or", BsonInt32(value))))
                let res = TransformResult.Update (fakeParser elem, (var, field, body), cont)
                Some res

            | _ -> None

        let traverse (x : MongoBuilder) expr =
            let rec traverse builder expr res =
                match transform builder expr with
                | Some trans ->
                    match trans with
                    | TransformResult.For (expr) ->
                        let x = TraverseResult.For (expr)
                        x :: res

                    | TransformResult.Query (f, args, cont) ->
                        let x = TraverseResult.Query (f, args)
                        traverse builder cont (x :: res)

                    | TransformResult.Update (f, args, cont) ->
                        let x = TraverseResult.Update (f, args)
                        traverse builder cont (x :: res)

                    | TransformResult.Pass (cont) -> traverse builder cont res

                | None -> res // REVIEW: raise an exception? return None?

            traverse x expr []

        member __.Source (x : #seq<'a>) : IMongoQueryable<'a> = invalidOp "not implemented"

        member __.For (source : IMongoQueryable<'a>, f : 'a -> IMongoQueryable<'a>) : IMongoQueryable<'a> = invalidOp "not implemented"

        member __.Zero () : IMongoQueryable<'a> = invalidOp "not implemented"

        member __.Yield x : IMongoQueryable<'a> = invalidOp "not implemented"

        member __.Quote (expr : Expr<#seq<'a>>) = expr

        member x.Run (expr : Expr<#seq<'a>>) : MongoOperationResult<'a> =

            let collection : Scope<'a> option ref = ref None
            // REVIEW: probably want to switch to an enum
            //         or discriminated union to handle the other potential operations,
            //         e.g. remove, replace, upsert, repsert
            let isUpdate = ref false

            let queryDoc = BsonDocument()
            let updateDoc = BsonDocument()

            let prepareDocs expr =
                traverse x expr
                |> List.iter (fun x ->
                    match x with
                    | TraverseResult.For (expr) ->
                        let obj =
                            // extracts the value from the For expression
                            // depends on whether was defined as a local variable
                            // (e.g. inside another function), or as a static one
                            // (e.g. binding at the module level)
                            match expr with
                            | Value (obj, _) -> Some obj
                            | PropertyGet (_, prop, _) -> Some (prop.GetValue null)
                            | _ -> None

                        collection :=
                            match obj with
                            | Some (:? Scope<'a> as x) -> Some x
                            | Some (:? IMongoCollection<'a> as x) -> Some (x.Find())

                            | Some _
                            | None -> None

                    | TraverseResult.Query (f, x) ->
                        match x ||> f with
                        | Some elem -> queryDoc.Add elem |> ignore
                        | None -> () // REVIEW: raise an exception?

                    | TraverseResult.Update (f, x) ->
                        isUpdate := true

                        match x |||> f with
                        | Some elem -> updateDoc.Add elem |> ignore
                        | None -> () // REVIEW: raise an exception?
                    )

            match expr with
            | SpecificCall <@ x.Defer @> (_, _, [ expr ]) ->
                prepareDocs expr

                let res =
                    if !isUpdate then // dereference
                        MongoDeferredOperation.Update (queryDoc, updateDoc)
                    else
                        MongoDeferredOperation.Query queryDoc

                MongoOperationResult.Deferred res

            | _ ->
                prepareDocs expr

                if !isUpdate then // dereference
                    match !collection with
                    | Some x ->
                        let res = x |> Scope.update updateDoc
                        MongoOperationResult.Update res

                    | None -> failwith "not given a scope"

                else
                    match !collection with
                    | Some x -> MongoOperationResult.Query x
                    | None -> failwith "not given a scope"

        [<CustomOperation("defer")>]
        member __.Defer (source : #seq<'a>) : IMongoDeferrable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("where", MaintainsVariableSpace = true)>]
        member __.Where (source : IMongoQueryable<'a>, [<ProjectionParameter>] predicate : 'a -> bool) : IMongoQueryable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("update", MaintainsVariableSpace = true)>]
        member __.Update (source : IMongoQueryable<'a>) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("inc", MaintainsVariableSpace = true)>]
        member __.Inc (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("dec", MaintainsVariableSpace = true)>]
        member __.Dec (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("set", MaintainsVariableSpace = true)>]
        member __.Set (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("unset", MaintainsVariableSpace = true)>]
        member __.Unset (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b option) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("addToSet", MaintainsVariableSpace = true)>]
        member __.AddToSet (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("popLeft", MaintainsVariableSpace = true)>]
        member __.PopLeft (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("popRight", MaintainsVariableSpace = true)>]
        member __.PopRight (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("pull", MaintainsVariableSpace = true)>]
        member __.Pull (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, predicate : 'b -> bool) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("pullAll", MaintainsVariableSpace = true)>]
        member __.PullAll (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, values : 'b list) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("push", MaintainsVariableSpace = true)>]
        member __.Push (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("addToSetEach", MaintainsVariableSpace = true)>]
        member __.AddToSetEach (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, values : 'b list) : IMongoEachUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("pushEach", MaintainsVariableSpace = true)>]
        member __.PushEach (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b list, values : 'b list) : IMongoEachUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("slice", MaintainsVariableSpace = true)>]
        member __.Slice (source : IMongoEachUpdatable<'a>, value : int) : IMongoEachUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("sortListBy", MaintainsVariableSpace = true)>]
        member __.SortListAscending (source : IMongoEachUpdatable<'a>, [<ProjectionParameter>] array : 'a -> 'b list, field: 'b -> 'c) : IMongoEachUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("sortListByDescending", MaintainsVariableSpace = true)>]
        member __.SortListDescending (source : IMongoEachUpdatable<'a>, [<ProjectionParameter>] array : 'a -> 'b list, field: 'b -> 'c) : IMongoEachUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("thenListBy", MaintainsVariableSpace = true)>]
        member __.ThenSortListAscending (source : IMongoEachUpdatable<'a>, [<ProjectionParameter>] array : 'a -> 'b list, field: 'b -> 'c) : IMongoEachUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("thenListByDescending", MaintainsVariableSpace = true)>]
        member __.ThenSortListDescending (source : IMongoEachUpdatable<'a>, [<ProjectionParameter>] array : 'a -> 'b list, field: 'b -> 'c) : IMongoEachUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("bitAnd", MaintainsVariableSpace = true)>]
        member __.BitAnd (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

        [<CustomOperation("bitOr", MaintainsVariableSpace = true)>]
        member __.BitOr (source : #IMongoUpdatable<'a>, [<ProjectionParameter>] field : 'a -> 'b, value : 'b) : IMongoUpdatable<'a> =
            invalidOp "not implemented"

    let mongo = new MongoBuilder()
