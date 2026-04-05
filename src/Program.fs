open System
open System.Diagnostics

open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.DependencyInjection

open Giraffe
open Giraffe.EndpointRouting

type Errors = string list

module Dto =
  [<CLIMutable>]
  type Note = {
    title: string
    tags: string list
    filename: string
  }

open Dto

module Zk =
  let private getNotebookPath () : Result<string, Errors> =
    let value = Environment.GetEnvironmentVariable "ZK_NOTEBOOK_PATH"

    if String.IsNullOrEmpty value then
      Error [ "\"ZK_NOTEBOOK_PATH\" variable is not defined" ]
    else
      Ok value

  let private runZkCommandAndDeserializeJsonResult<'T> (notebookPath: string) (command: string) : Result<'T, Errors> =
    let commandWithJsonFormat = sprintf "%s --format=json" command

    try
      let processInfo = ProcessStartInfo("zk", commandWithJsonFormat)
      processInfo.WorkingDirectory <- notebookPath
      processInfo.UseShellExecute <- false
      processInfo.CreateNoWindow <- true
      processInfo.RedirectStandardOutput <- true
      processInfo.RedirectStandardError <- true
      processInfo.RedirectStandardInput <- false
      let p = Process.Start processInfo
      p.OutputDataReceived.Add ignore
      p.ErrorDataReceived.Add ignore
      p.EnableRaisingEvents <- true
      p.Exited.Add(fun _ -> p.Dispose())

      if p.Start() then
        p.StandardOutput.ReadToEnd()
        |> System.Text.Json.JsonSerializer.Deserialize<'T>
        |> Ok
      else
        Error [ "Process could not be started" ]
    with ex ->
      Error [ ex.Message ]

  let list (query: string option) : Result<Note list, Errors> =
    getNotebookPath ()
    |> Result.bind (fun notebookPath ->
      runZkCommandAndDeserializeJsonResult<Note list>
        notebookPath
        (query |> Option.map (fun q -> $"list {q}") |> Option.defaultValue "list"))

// TODO: deal with empty result
let handleList: HttpHandler =
  fun (next: HttpFunc) (ctx: HttpContext) ->
    let query =
      match ctx.Request.Query.["query"] with
      | values when values.Count > 0 -> values.[0] |> Some
      | _ -> None

    match Zk.list query with
    | Ok notes -> json notes next ctx
    | Error errors -> ServerErrors.internalError (json errors) next ctx

let endpoints: Endpoint list = [ GET [ route "list" handleList ] ]

let notFoundHandler = "Not found" |> text |> RequestErrors.notFound

let configureServices (services: IServiceCollection) =
  services.AddRouting().AddGiraffe() |> ignore

let configureApp (appBuilder: IApplicationBuilder) =
  appBuilder.UseRouting().UseGiraffe(endpoints).UseGiraffe notFoundHandler
  |> ignore

[<EntryPoint>]
let main args =
  let builder = WebApplication.CreateBuilder args
  configureServices builder.Services
  let app = builder.Build()
  configureApp app
  app.Run()
  0
