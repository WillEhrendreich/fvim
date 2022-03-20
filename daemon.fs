module FVim.daemon

open log
open common
open getopt

open FSharp.Span.Utils
open System
open System.Diagnostics
open System.IO
open System.IO.Pipes
open System.Runtime.InteropServices
open System.Security.Principal
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading.Tasks

type Session =
  {
    id: int
    // None=Free to connect
    // Some=Exclusively connected
    server: NamedPipeServerStream option
    proc: Process
    exitHandle: IDisposable
  }

let private sessions = hashmap []
let mutable private sessionId = 0
let private FVR_MAGIC = [| 0x46uy ; 0x56uy ; 0x49uy ; 0x4Duy |]

[<Literal>]
let private FVR_NO_FREE_SESSION = -1
[<Literal>]
let private FVR_SESSION_NOT_FOUND = -2
[<Literal>]
let private FVR_CLIENT_EXCEPTION = -10

let getErrorMsg =
  function
  | FVR_NO_FREE_SESSION -> "No attachable free session available."
  | FVR_SESSION_NOT_FOUND -> "The specified session is not found."
  | FVR_CLIENT_EXCEPTION -> "Could not connect to the session server."
  | _ -> "Unknown error."

let private jsonopts = JsonSerializerOptions()
jsonopts.Converters.Add(JsonFSharpConverter())
let inline private serialize x = JsonSerializer.Serialize(x, jsonopts)
let inline private deserialize<'a> (x: string) = JsonSerializer.Deserialize<'a>(x, jsonopts)

let inline private trace x = trace "daemon" x

let pipeaddrUnix x = "/tmp/CoreFxPipe_" + x
let pipeaddr x =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    then @"\\.\pipe\" + x
    else pipeaddrUnix x

let pipename (x:'a) = $"fvr_{x}"

let defaultDaemonName = pipename "main"

let attachSession id svrpipe =
  match sessions.TryGetValue id with
  | true, ({ server = None } as s) -> 
    let ns = {s with server = Some svrpipe}
    sessions.[id] <- ns
    Ok ns
  | _ -> Error FVR_SESSION_NOT_FOUND

let newSession nvim stderrenc args svrpipe = 
  let myid = sessionId

  let pname = pipename myid
  let paddr = pipeaddr pname
  let args = "--headless" :: "--listen" :: paddr :: args
  let proc = newProcess nvim args stderrenc
  let session = 
    { 
      id = myid 
      server = Some svrpipe
      proc = proc
      exitHandle =  proc.Exited |> Observable.subscribe(fun _ -> 
        // remove the session
        trace "Session %d terminated" myid
        sessions.[myid].exitHandle.Dispose()
        sessions.Remove(myid) |> ignore
        proc.Dispose()
        )
    }

  sessionId <- sessionId + 1
  sessions.[myid] <- session
  proc.Start() |> ignore
  Ok session


let attachFirstSession svrpipe =
  sessions |> Seq.tryFind (fun kv -> kv.Value.server.IsNone)
  >>= (fun kv ->
    let ns = {kv.Value with server = Some svrpipe}
    sessions.[kv.Key] <- ns
    Some ns)
  |> function | Some ns -> Ok ns | None -> Error FVR_NO_FREE_SESSION

let serveSession (session: Session) =
  task {
    let pname = pipename session.id
    use client = new NamedPipeClientStream(".", pname, IO.Pipes.PipeDirection.InOut, IO.Pipes.PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation)
    do! client.ConnectAsync()
    trace "Connected to NeoVim server at %s" pname
    let fromNvim = client.CopyToAsync(session.server.Value)
    let toNvim = session.server.Value.CopyToAsync(client)
    let! _ = Task.WhenAny [| fromNvim; toNvim |]
    // Something is completed, let's investigate why
    if not session.proc.HasExited then
      // the NeoVim server is still up and running
      sessions.[session.id] <- { session with server = None }
      trace "Session %d detached" session.id
    return ()
  }

let serve nvim stderrenc (pipe: NamedPipeServerStream) = 
  backgroundTask {
    try
      let rbuf = Array.zeroCreate 8192
      let rmem = rbuf.AsMemory()
      // read protocol header
      // [magic header FVIM] 4B
      // [payload len] 4B, little-endian
      do! read pipe rmem.[0..7]
      if rbuf.[0..3] <> FVR_MAGIC then 
        trace "Incorrect handshake magic. Got: %A" rbuf.[0..3]
        return()
      let len = rbuf.[4..7] |> toInt32LE
      if len >= rbuf.Length || len <= 0 then 
        trace "Invalid payload length %d" len
        return()
      do! read pipe rmem.[0..len-1]

      let request: FVimRemoteVerb = 
        (rbuf, 0, len)
        |> Text.Encoding.UTF8.GetString
        |> deserialize
      trace "Payload=%A" request
      let session = 
        match request with
        | NewSession args -> newSession nvim stderrenc args pipe
        | AttachTo id -> attachSession id pipe
        | AttachFirst -> attachFirstSession pipe

      match session with
      | Error errno -> 
        trace "Session unavailable for request %A, errno=%d" request errno
        do! fromInt32LE errno |> readonlymemory |> write pipe
        return()
      | Ok session -> 
        trace "Request %A is attaching to session %d" request session.id
        do! fromInt32LE session.id |> readonlymemory |> write pipe
        do! serveSession session
    finally
      try
        pipe.Dispose()
      with ex -> trace "%O" ex
  }

let daemon (pname: string option) (nvim: string) (stderrenc: Text.Encoding) =
    trace "Running as daemon."
    let pname = pname |> Option.defaultValue defaultDaemonName
    let paddr = pipeaddr pname
    trace "FVR server address is '%s'" paddr

    while true do
      let svrpipe =
          new NamedPipeServerStream(pname, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
      svrpipe.WaitForConnection()
      trace "Incoming connection."
      serve nvim stderrenc svrpipe |> ignore
    0

let fvrConnect (stdin: Stream) (stdout: Stream) (verb: FVimRemoteVerb) =
  let payload = 
    verb
    |> serialize
    |> Text.Encoding.UTF8.GetBytes
  let intbuf = fromInt32LE payload.Length
  try
    stdin.Write(FVR_MAGIC, 0, FVR_MAGIC.Length)
    stdin.Write(intbuf, 0, intbuf.Length)
    stdin.Write(payload, 0, payload.Length)
    stdin.Flush()
    // this doesn't drive the task:
    // (read stdout (intbuf.AsMemory())).Wait()
    // this *does* drive the task:
    read stdout (intbuf.AsMemory()) |> Async.AwaitTask |> Async.StartImmediate
    toInt32LE intbuf
  with ex ->
    trace "%O" ex
    FVR_CLIENT_EXCEPTION
