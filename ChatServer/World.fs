﻿module ChatServer.World

open Akka.FSharp
open Akka.Actor

//TODO: Need 3PCState - Aborted, Unknown, Committable, Commited
//TODO: Need an iteration count
type RoomState = {
    actors: Set<IActorRef>
    coordinator: IActorRef option
    master: IActorRef option
    beatmap: Map<string,Member*IActorRef*int64>
    beatCancels: List<ICancelable>
    commitPhase: CommitPhase
    commitIter: int
    songList: Map<string, string>
}

and Member =
    | Participant
    | Coordinator
    | Observer

and CommitPhase =
    | Start
    | FirstTime
    | CoordWaiting
    | CoordInitCommit of Update * Map<string, IActorRef> * Set<IActorRef>
    | CoordCommitable of Update * Map<string, IActorRef> * Set<IActorRef>
    | CoordCommitted
    | CoordAborted
    | ParticipantInitCommit of Update * Map<string, IActorRef>
    | ParticipantCommitable of Update * Map<string, IActorRef>
    | ParticipantCommitted
    | ParticipantAborted

and DecisionMsg =
    | Abort
    | Commit

and Update =
    | Add of string * string
    | Delete of string

type RoomMsg =
    | Join of IActorRef
    | JoinMaster of IActorRef
    | RequestFullState
    | FullStateRequest of IActorRef
    | FullState
    | DetermineCoordinator
    | Heartbeat of string * Member * IActorRef
    | AddSong of string * string
    | DeleteSong of string
    | VoteReply of VoteMsg * IActorRef
    | VoteReplyTimeout of int
    | AckPreCommit of IActorRef
    | AckPreCommitTimeout of int
    | StateReqReply of IActorRef * CommitState
    | StateReqReplyTimeout of int
    | VoteReq of Update
    | PreCommit
    | PreCommitTimeout of int
    | CommitTimeout of int
    | Decision of DecisionMsg
    | StateReq of IActorRef
    | StateReqTimeout of int
    | GetSong of string
    | Leave of IActorRef

and VoteMsg =
    | Yes
    | No

and CommitState =
    | Aborted
    | Uncertain
    | Committable
    | Committed


let sw =
    let sw = System.Diagnostics.Stopwatch()
    sw.Start()
    sw

let scheduleRepeatedly (sender:Actor<_>) rate actorRef message =
    sender.Context.System.Scheduler.ScheduleTellRepeatedlyCancelable(
        System.TimeSpan.FromMilliseconds 0.,
        System.TimeSpan.FromMilliseconds rate,
        actorRef,
        message,
        sender.Self)

let scheduleOnce (sender:Actor<_>) after actorRef message =
    sender.Context.System.Scheduler.ScheduleTellOnceCancelable(
        System.TimeSpan.FromMilliseconds (float after),
        actorRef,
        message,
        sender.Self)

let room selfID beatrate aliveThreshold (mailbox: Actor<RoomMsg>) =
    let rec loop state = actor {

        // Cancel all previously set heartbeats and start anew
        let startHeartbeat membString state =
            state.beatCancels
            |> List.iter (fun c -> c.Cancel())
            let beatCancels =
                state.actors
                |> Set.toList
                |> List.map (fun actorRef ->
                    scheduleRepeatedly mailbox beatrate actorRef membString)
            { state with beatCancels = beatCancels }
        
        let startCoordinatorHeartbeat state =
            startHeartbeat (sprintf "coordinator %s" selfID) state
                
        let startParticipantHeartbeat state =
            printfn "Started participant heartbeat"
            startHeartbeat (sprintf "participant %s" selfID) state

        let startObserverHeartbeat state =
            startHeartbeat (sprintf "observer %s" selfID) state
        
        let filterAlive map =
            map
            |> Map.filter (fun _ (_,_,ms) -> (sw.ElapsedMilliseconds - ms) < aliveThreshold)

        // Get a map (id, ref) of all the alive processes
        let getAliveMap state =
            state.beatmap
            |> filterAlive
            |> Map.map (fun id (_,ref,_) -> ref)
        
        let getAlive membType state =
            state.beatmap
            |> filterAlive
            |> Map.filter (fun id (memb, _, _) -> memb = membType)
            |> Map.map (fun id (_,ref,_) -> ref)


        // Sends a message to self after the timeout threshold
        let setTimeout (message: RoomMsg) =
            scheduleOnce mailbox aliveThreshold mailbox.Self message
            |> ignore

        let initiateElectionProtocol state =
            // Find a new coordinator
            let aliveParticipants =
                state
                |> getAlive Participant
            let (potentialCoordId,potentialCoordRef) =
                aliveParticipants
                |> Map.fold (fun (lowest, _) id ref -> if (int id) < (int lowest) then (id, ref) else (lowest, ref)) (selfID, mailbox.Self)
            if potentialCoordId <> selfID then
                // This process is not the new potential coordinator
                setTimeout <| StateReqTimeout state.commitIter
                { state with coordinator = Some potentialCoordRef }
            else
                // This process is the new coordinator
                aliveParticipants
                |> Map.iter (fun _ ref -> ref <! "statereq")
                setTimeout <| StateReqReplyTimeout state.commitIter
                // Start heartbeating as the coordinator
                startCoordinatorHeartbeat { state with coordinator = Some mailbox.Self }

        
        let! msg = mailbox.Receive()
        let sender = mailbox.Sender()

        match msg with
        | Join ref ->
            printfn "In Join"
            let newCancel =
                match state.commitPhase with
                | Start ->
                    printfn "In Join -> Start"
                    //TODO: Change back to NoCommit and Observer after a commit
                    scheduleRepeatedly mailbox beatrate ref (sprintf "observer %s" selfID)
                | _ ->
                    printfn "In Join -> _ (not in Start)"
                    match state.coordinator with
                    | Some ref' when ref' = mailbox.Self ->
                        printfn "In Join -> _ -> Some coordinator"
                        scheduleRepeatedly mailbox beatrate ref (sprintf "coordinator %s" selfID)
                    | _ ->
                        //TODO: What if state.coordinator is none?
                        printfn "In Join -> _ -> _ (No coodinator)"
                        scheduleRepeatedly mailbox beatrate ref (sprintf "participant %s" selfID)

            let state' =
                { state with
                    actors = Set.add ref state.actors ;
                    beatCancels = newCancel :: state.beatCancels}
           
            return! loop state'

        | JoinMaster ref ->
            return! loop { state with master = Some ref }

        | Heartbeat (id, memb, ref) ->
            return! loop {
                state with
                    beatmap = state.beatmap |> Map.add id (memb, ref, sw.ElapsedMilliseconds) ;
                    coordinator = match memb with
                                  | Coordinator -> Some ref
                                  | _ -> state.coordinator }
        
        | RequestFullState ->
            // Find any alive participant/observer for their state 
            // TODO: Change behaviour? Maybe just make every alive process send state on join
            let aliveRef =
                state.beatmap
                |> Map.tryPick (fun _ (_, ref, lastMs) -> if (lastMs < aliveThreshold) then Some ref else None)
            match aliveRef with
            | Some ref -> ref <! "FullStateRequest"
            | None -> failwith "Ref not found in beatmap in RequestFullState"

        | FullStateRequest ref ->
            //TODO: send more state
            ref <! sprintf "songlist %A" (Map.toList state.songList)
        
        | FullState ->
            //TODO: Handle receiving state
            ()

        | DetermineCoordinator ->
            let state' =
                match state.coordinator with
                | None ->
                    // Check if 3PC is going on
                    let is3PC =
                        state.beatmap
                        |> Map.filter (fun _ (memb, _, lastMs) -> (memb = Participant) && (lastMs < aliveThreshold))
                        |> Map.isEmpty
                        |> not
                    //TODO: What if participants are alive but not in 3PC?
                    // If not, elect self as coordinator
                    if not is3PC then
                        printfn "%s is the coordinator" selfID
                        match state.master with
                        | Some m -> m <! sprintf "coordinator %s" selfID
                        | None -> printfn "WARNING: No master in DetermineCoordinator"
                        startCoordinatorHeartbeat { state with coordinator = Some mailbox.Self; commitPhase = CoordWaiting }
                    else
                        state
                | _ ->
                    state
            return! loop state'

        | AddSong (name, url) ->
            printfn "In AddSong"
            // Current process is the coordinator
            let state' =
                if (String.length url > int selfID + 5) then
                    printfn "Aborted because the url doesn't satisfy the length condition"
                    { state with
                        commitPhase = CoordAborted }
                else
                    printfn "Trying to get alive map"
                    // Get a snapshot of the upSet
                    let upSet = getAliveMap state
                    printfn "The upSet is %O" upSet
            
                    // Initiate 3PC with all alive participants by sending VoteReq
                    upSet
                    |> Map.iter (fun _ r -> r <! (sprintf "votereq add %s %s" name url))
                    
                    printfn "Before Vote reply timeout"
                    // Wait for Votes or Timeout
                    setTimeout <| VoteReplyTimeout state.commitIter
                    |> ignore
                    { state with
                        commitPhase = CoordInitCommit (Add (name,url), upSet, Set.empty) }
            
            return! loop state'

        | DeleteSong name ->
            // Current process is the coordinator
            // Get a snapshot of the upSet
            let state' =
                let upSet = getAliveMap state
                let upListIds =
                    upSet
                    |> Map.toList
                    |> List.map (fun (id, _) -> id)
            
                // Initiate 3PC with all alive participants by sending VoteReq
                upSet
                |> Map.iter (fun _ r -> r <! (sprintf "votereq delete %s" name))
            
                // Wait for Votes or Timeout
                setTimeout <| VoteReplyTimeout state.commitIter
                |> ignore
                { state with
                    commitPhase = CoordInitCommit ((Delete name), upSet, Set.empty) }
            
            return! loop state'
        
        | VoteReply (vote, ref) ->
            printfn "In VoteReply"
            let state' =
                match state.commitPhase with
                | CoordInitCommit (update, upSet, voteSet) ->
                    match vote with
                    | Yes ->
                        printfn "Received a yes vote"
                        let voteSet' = Set.add ref voteSet
                        // Check if we've received all votes
                        if Set.count voteSet' = Map.count upSet then
                            printfn "Received all votes"
                            upSet
                            |> Map.iter (fun _ ref -> ref <! "precommit")
                            setTimeout <| AckPreCommitTimeout state.commitIter
                            { state with 
                                commitPhase = CoordCommitable (update, upSet, voteSet')}
                        else
                            printfn "Didn't receive all votes"
                            // If not, just add new vote to voteset'
                            { state with 
                                commitPhase = CoordInitCommit (update, upSet, voteSet')}
                    | No ->
                        printfn "Received a no vote"
                        upSet
                        |> Map.filter (fun _ ref -> not (ref = mailbox.Sender())) // Don't send abort to the process that voted no
                        |> Map.iter (fun _ ref -> ref <! "abort")
                        match state.master with
                        | Some m -> m <! "ack abort"
                        | None -> printfn "WARNING: No master in VoteReply"
                        startObserverHeartbeat {
                            state with
                                commitPhase = CoordAborted
                                commitIter = state.commitIter + 1 }
                | _ ->
                    printfn "WARNING: Invalid state in vote reply"
                    state
            return! loop state'
                        
        | VoteReplyTimeout sourceIter ->
            printfn "In VoteReplyTimeout"
            let state' =
                match state.commitPhase with
                | CoordInitCommit (update, upSet, voteSet) ->
                    if Set.count voteSet = Map.count upSet then
                        printfn "Since there is no participant, just decide commit for self"
                        if (Map.count upSet) = 0 then
                            // If the coordinator is the only server alive, just commit the decision
                                // Send a commit to master
                                match state.master with
                                | Some m ->
                                    printfn "Sending a commit to master"
                                    m <! "ack commit"
                                | None ->
                                    printfn "WARNING: No master"
                                match update with
                                | (Add (name, url)) when sourceIter = state.commitIter ->
                                    { state with
                                        songList = Map.add name url state.songList
                                        commitIter = state.commitIter + 1
                                        commitPhase = CoordCommitted }
                                | (Delete name) when sourceIter = state.commitIter ->
                                    { state with
                                        songList = Map.remove name state.songList
                                        commitIter = state.commitIter + 1
                                        commitPhase = CoordCommitted }
                                | _ ->
                                    printfn "Received a VoteReplyTimeout in a later iteration"
                                    state
                        else
                            printfn "In VoteReplyTimeout but have already received all votes."
                            state
                    else
                        // We did not receive all vote replies
                        printfn "Didn't receive all vote replies"
                        upSet
                        |> Map.iter (fun _ ref -> ref <! "abort")
                        match state.master with
                        | Some m -> m <! "ack abort"
                        | None -> printfn "WARNING: No master in VoteReply"
                        startObserverHeartbeat {
                            state with
                                commitPhase = CoordAborted
                                commitIter = state.commitIter + 1 }
                | s ->
                    printfn "WARNING: Invalid state in VoteReplyTimeout: %O" s
                    state
            return! loop state'
        
        | AckPreCommit ref ->
            printfn "In AckPreCommit"
            let state =
                match state.commitPhase with
                | CoordCommitable (decision, upSet, ackSet) ->
                        let ackSet' = Set.add ref ackSet
                        if Set.count ackSet = Map.count upSet then
                            printfn "Received all Acks"
                            ackSet
                            |> Set.iter (fun ref -> ref <! "commit")
                            match state.master with
                            | Some m ->
                                printfn "Sending a commit"
                                m <! "ack commit"
                            | None -> printfn "WARNING: No master in AckPreCommit"
                            match decision with
                            | Add (name, url) ->
                                { state with
                                    songList = Map.add name url state.songList
                                    commitIter = state.commitIter + 1
                                    commitPhase = CoordCommitted}
                            | Delete name ->
                                { state with
                                    songList = Map.remove name state.songList
                                    commitIter = state.commitIter + 1
                                    commitPhase = CoordCommitted}
                        else
                            printfn "Didn't recieve all Acks yet"
                            { state with commitPhase = CoordCommitable (decision, upSet, ackSet') }
                | CoordCommitted ->
                    printfn "WARNING: Some votes were ignored because they arrived after timeout threshold.\n"
                    state
                | _ ->
                    printfn "WARNING: Invalid commit state in AckPreCommit"
                    state
            return! loop state
        
        | AckPreCommitTimeout sourceIter ->
            printfn "In AckPreCommitTimeout"
            let state' =
                match state.commitPhase with
                | CoordCommitable (decision, upSet, ackSet) ->
                        if Set.count ackSet = Map.count upSet then
                            printfn "In AckPreCommitTimeout but have already received all votes."
                            state
                        else
                            // Commit to the processes that have ack'd
                            ackSet
                            |> Set.iter (fun ref -> ref <! "commit")
                            match state.master with
                            | Some m -> m <! "ack commit"
                            | None -> printfn "WARNING: No master in AckPreCommit"
                            match decision with
                            | Add (name, url) when state.commitIter = sourceIter ->
                                { state with
                                    songList = Map.add name url state.songList
                                    commitIter = state.commitIter + 1
                                    commitPhase = CoordCommitted }
                            | Delete name when state.commitIter = sourceIter ->
                                { state with
                                    songList = Map.remove name state.songList
                                    commitIter = state.commitIter + 1
                                    commitPhase = CoordCommitted }
                            | _ ->
                                printfn "Received an AckPreCommitTimeout in a later iteration"
                                state
                | _ ->
                    printfn "Warning: Invalid state in AckPreCommitTimeout"
                    state
            return! loop state'

        | VoteReq update ->
            printfn "In VoteReq on iteration %i" state.commitIter
            let upSet =
                getAliveMap state
            // TODO: should votereq contain the coordinator ref?
            // Decide vote according to the rule
            let vote =
                match update with
                | Add (_, url) ->
                    not (String.length url > int selfID + 5)
                | Delete _ ->
                    true
            printfn "Voted %s" (if vote then "yes" else "no")
            // Reply to the coordinator with the vote
            match state.coordinator with
            | Some c -> c <! sprintf "votereply %s" (if vote then "yes" else "no")
            | None -> failwith "No coordinator in VoteReq"
            
            let state' =
                if vote then
                    // Wait for precommit or timeout
                    setTimeout <| PreCommitTimeout state.commitIter
                    { state with
                        commitPhase = ParticipantInitCommit (update, upSet) }
                else
                    startObserverHeartbeat {
                        state with
                            commitPhase = ParticipantAborted }
            // Start heartbeating as a participant
            return! loop state'

        | PreCommit ->
            printfn "In PreCommit"
            match state.coordinator with
            | Some c -> c <! "ackprecommit"
            | None -> failwith "No Coordinator in PreCommit"

            match state.commitPhase with
            | ParticipantInitCommit (update, upSet) ->
                let state' = {
                    state with
                        commitPhase = ParticipantCommitable (update, upSet)}
            
                // Wait for commit or timeout
                setTimeout <| CommitTimeout state.commitIter
            
                return! loop state'
            | _ ->
                failwith "Invalid commit state in Precommit"
        
        | PreCommitTimeout sourceIter ->
            printfn "In PreCommitTimeout from iteration %i" sourceIter
            let state' =
                // The coordinator may have died
                match state.coordinator with
                | Some c -> 
                    let isCoordinatorDead =
                        getAliveMap state
                        |> Map.exists (fun id ref -> ref = c)
                        |> not
                    if isCoordinatorDead then
                        initiateElectionProtocol state
                    else
                        state
                | None ->
                    printfn "WARNING: Received a VoteRequest without a coordinator"
                    state
            return! loop state'

        | Decision decision ->
            printfn "In Decision"
            let state' =
                match decision with
                | Abort ->
                    printfn "In Decision -> Abort"
                    startObserverHeartbeat {
                        state with
                            commitPhase = ParticipantAborted
                            commitIter = state.commitIter + 1 }
                | Commit ->
                    printfn "In Decision -> Commit"
                    match state.commitPhase with
                    | ParticipantCommitable (Add (name, url), upSet) ->
                        printfn "In Decision -> Commit -> ParticipantCommitable Add"
                        startObserverHeartbeat {
                            state with
                                songList = Map.add name url state.songList
                                commitIter = state.commitIter + 1
                                commitPhase = ParticipantCommitted }
                    | ParticipantCommitable (Delete name, upSet) ->
                        printfn "In Decision -> Commit -> ParticipantCommitable Delete"
                        startObserverHeartbeat {
                            state with
                                songList = Map.remove name state.songList
                                commitIter = state.commitIter + 1
                                commitPhase = ParticipantCommitted }
                    | _ -> failwith (sprintf "Invalid commit state in Decision: %O" state.commitPhase)
             
            return! loop state'

        | CommitTimeout sourceIter -> 
            printfn "In CommitTimeout from iteration %i" sourceIter
            // The coordinator may have died
            return! loop state

        | StateReq ref ->
            let state' =
                match state.commitPhase with
                | ParticipantInitCommit (update, upSet) ->
                | ParticipantCommitable of Update * Map<string, IActorRef> ->
                | ParticipantCommitted ->
                | ParticipantAborted ->
                | s ->
                    printfn "ERROR: Invalid commit phase in StateReq - %O" s
                    state
            return! loop state'
        | StateReqTimeout commitIter ->
            let state' =
                if state.commitIter = commitIter then
                    match state.coordinator with
                    | Some c ->
                        let isCoordinatorAlive =
                            state
                            |> getAliveMap
                            |> Map.exists (fun _ ref -> ref = c)
                        if isCoordinatorAlive then
                            state
                        else
                            // Try again
                            initiateElectionProtocol state
                    | None ->
                        printfn "ERROR: No coordinator set in StateReqTimeout"
                        state
                else
                    state
            return! loop state'

        | GetSong name ->
            printfn "In GetSong"
            let url =
                state.songList
                |> Map.tryFind name
                |> Option.defaultValue "NONE"

            match state.master with
            | Some m -> m <! (sprintf "resp %s" url)
            | None -> printfn "WARNING: No master in GetSong"
            
            return! loop state

        | Leave ref ->
            return! loop { state with actors = Set.remove ref state.actors }
    }

    //TODO: Check if somebody is alive and ask for state
    //TODO: Decide what the state transmission should contain
    //scheduleOnce mailbox aliveThreshold mailbox.Self RequestFullState
    //|> ignore

    //TODO: Check if DT Log exists if nobody is alive

    // Concurrently, try to determine whether a coordinator exists
    // TODO: Replace 3000 with parameter
    scheduleOnce mailbox 3000L mailbox.Self DetermineCoordinator
    |> ignore
    
    // TODO: Read from DTLog

    loop {
        actors = Set.empty ;
        coordinator = None ;
        master = None ;
        beatmap = Map.empty ;
        songList = Map.empty ;
        commitPhase = Start ;
        commitIter = 1 ;
        beatCancels = [] }