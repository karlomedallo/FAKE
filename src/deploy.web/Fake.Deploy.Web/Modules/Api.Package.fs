﻿namespace Fake.Deploy.Web.Module
open System
open System.IO
open System.Net
open log4net
open Nancy
open Nancy.ModelBinding
open Nancy.Security
open Fake.ProcessHelper
open Fake.FakeDeployAgentHelper
open Fake.Deploy.Web
open Fake.Deploy.Web.Data
open Fake.Deploy.Web.Module.NancyOp
open Fake.Deploy.Web.Module.ApiModels

type DeployMessage = { IsError : bool; Message : string; Timestamp : DateTime }

type ApiPackage (dataProvider : IDataProvider) as http =
    inherit FakeModule("/api/v1/package")

    let logger = LogManager.GetLogger(http.GetType().Name)

    let packageTemp = Path.Combine(appdata.FullName, "Package_Temp")

    do
        http.post "/rollback" (fun p ->
            let body = http.Bind<ApiModels.RollbackRequest>()
            try
               match rollbackTo (body.agentUrl.Trim('/') + "/fake/") body.appName body.version with
               | Failure(err) -> 
                   http.Response.AsText("").WithStatusCode HttpStatusCode.InternalServerError
               | Success a -> 
                    http.Response
                        .AsText("")
                        .WithStatusCode HttpStatusCode.NoContent
               | _ -> 
                    http.Response
                        .AsText("Unexpected response")
                        .WithStatusCode HttpStatusCode.InternalServerError
            with e ->
                logger.Error("An error occured rolling back package",e)
                http.InternalServerError e
        )

        http.post "/deploy" (fun p ->
            try
                let agentId = http.Request.Form ?> "agentId"
                let agent = dataProvider.GetAgents [agentId] |> Seq.head
                let url = agent.Address.AbsoluteUri + "fake/"
                let env = 
                    [agent.EnvironmentId] 
                    |>dataProvider.GetEnvironments 
                    |> Seq.head
                    |> fun x -> x.Name
                    |> sprintf "env=%s"
                Directory.CreateDirectory(packageTemp) |> ignore
                let files = 
                    http.Request.Files
                    |> Seq.map(fun file ->
                        let filename = Path.Combine(packageTemp, file.Name)
                        use filestream = new FileStream(filename, FileMode.Create)
                        file.Value.CopyTo(filestream)
                        filename)
                let code, message = 
                    files
                    |> Seq.map(fun file ->
                        match postDeploymentPackage url file [|env|] with
                        | Failure(err) -> 
                            file, Some err, HttpStatusCode.InternalServerError, Some(err)
                        | Success a -> 
                            if File.Exists(file) then File.Delete(file)
                            file, None, HttpStatusCode.Created, Some a
                        | _ -> 
                            file, None, HttpStatusCode.InternalServerError, None
                    )
                    |> Seq.fold(fun state t ->
                        let file, ex, httpCode, res = t
                        let code = if httpCode > fst state then httpCode else fst state
                        let msg = match res with
                                    | None -> (snd state)
                                    | Some a -> (snd state) @ (a.Messages |> List.ofSeq)
                        (code, msg)
                        ) (HttpStatusCode.OK, [])
                http.Response
                    .AsJson(message |> List.map(fun m -> { IsError = m.IsError; Message = m.Message; Timestamp = m.Timestamp.LocalDateTime }))
                    .WithStatusCode code
            with e ->
                logger.Error("An error occured deploying package", e)
                http.InternalServerError e
        )