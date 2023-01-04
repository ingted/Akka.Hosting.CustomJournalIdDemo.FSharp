﻿namespace FAkka.Http


open Akka.Cluster.Sharding
open Akka.Util



open Akka.Actor
open Akka.Event

open Akka.Persistence

module Messages =
    open System

    [<Interface>] 
    type IWithUserId =
        abstract member UserId : string

    type UserDescriptor(UserId:string, UserName:string) =
        interface IWithUserId with
            member this.UserId = UserId
    
        member this.UserName = UserName
        static member val Empty = new UserDescriptor(String.Empty, String.Empty) with get
    
    type UserCreatedEvent(Descriptor:UserDescriptor, Timestamp:int64) =
        
        member this.Timestamp = Timestamp
        member this.Descriptor = Descriptor
        interface IWithUserId with
            member this.UserId = (Descriptor :> IWithUserId).UserId


    type FetchUsers () =
        static member Instance = new FetchUsers();
        member this.FetchUsers() = ()


    type FetchUser (userId:string) =
        interface IWithUserId with
            member this.UserId = userId


    type CreateUser(Descriptor:UserDescriptor) =
        interface IWithUserId with
            member this.UserId = (Descriptor :> IWithUserId).UserId
        member this.Descriptor = Descriptor

    type ResponseKind =
    | Success 
    | Failure
    | Unknown

    type CommandResponse (ResponseKind:ResponseKind) = 
        member this.ResponseKind = ResponseKind

module Actors =
    open Messages
    open System
    open System.Collections.Generic
    open System.Collections.Immutable
    open Akka.Actor
    open Akka.Persistence.Query
    open Akka.Persistence.Query.Sql
    open Akka.Streams
    open Akka.Streams.Dsl
    
    type UserActionsEntity (persistenceId:string) as this =
        inherit ReceivePersistentActor ()
        let mutable cs = UserDescriptor.Empty
        let Context = ReceivePersistentActor.Context
        let log = Context.GetLogger()
        do            
            this.Recover<UserCreatedEvent> (fun (c:UserCreatedEvent) ->
                cs <- c.Descriptor
                log.Info($"Recovered {c}", [||])
            )

            
            this.Command<CreateUser>(fun (user:CreateUser) ->
                let e = new UserCreatedEvent(user.Descriptor, DateTime.UtcNow.Ticks)
                this.Persist(e, fun evt ->
                
                    log.Info($"Persisted {evt}", [||])
                    cs <- evt.Descriptor;
                    if (not <| Context.Sender.IsNobody()) then
                        Context.Sender.Tell(new CommandResponse(ResponseKind.Success))
                )
            )

            this.Command<FetchUser>(fun user ->
                Context.Sender.Tell(cs)
            )


        member this.CurrentState 
            with get () = cs
            and set (v) = cs <- v
        override this.PersistenceId = persistenceId
        static member Props(userId:string) =
            Props.Create(fun _ -> new UserActionsEntity(userId))
    
    
    type Indexer (userActionsShardRegion:IActorRef) as this =
        inherit ReceiveActor ()
        let Context = ReceiveActor.Context
        let _log = Context.GetLogger()
        let _userActionsShardRegion = userActionsShardRegion

        let _users = new Dictionary<string, UserDescriptor>()

        do

            this.Receive<UserDescriptor>(fun (d:UserDescriptor) ->            
                _log.Info($"Found {d}", [||])
                _users[(d :> IWithUserId).UserId] <- d
            )

            this.Receive<FetchUsers>(fun (f:FetchUsers) -> 
                Context.Sender.Tell(_users.Values.ToImmutableList())
                )

            this.Receive<string>(fun s -> 
                _log.Info("Recorded completion of the stream")
                )

            this.Receive<UserCreatedEvent>(fun (e:UserCreatedEvent) ->
                _userActionsShardRegion.Ask<UserDescriptor>(
                    new FetchUser((e :> IWithUserId).UserId), TimeSpan.FromSeconds(1)
                    ).PipeTo(Context.Self)
                |> ignore
                )

        override this.PreStart() =
        
            this.FetchIds()
        

        member this.FetchIds() =
    
            let readJournal = 
                Context.System.ReadJournalFor<SqlReadJournal>(SqlReadJournal.Identifier)
            let r = 
                Sink.ActorRef<UserCreatedEvent>(Context.Self, "complete")
                :> IGraph<SinkShape<UserCreatedEvent>, Akka.NotUsed>
            let srj0 =
                readJournal.AllEvents()
                    .Where(fun e -> 
                        match e.Event with
                        | :? UserCreatedEvent -> true
                        | _ -> false)
                    .Select(fun uc -> uc.Event :?> UserCreatedEvent)
                    .WithAttributes(ActorAttributes.CreateSupervisionStrategy(fun e -> Supervision.Directive.Restart))
            
            srj0.RunWith<Akka.NotUsed>(r, Context.Materializer())
    
    

  
  



module CustomJournalIdDemo =
    open Messages
    open Microsoft.Extensions.Hosting
    open System
    open System.Threading
    open System.Threading.Tasks
    open Akka.Hosting
    open Actors
    type UserMessageExtractor (maxNumberOfShards:int) =
        inherit HashCodeMessageExtractor (maxNumberOfShards)


        override this.EntityId(message : obj) =
            match message with
            | :? IWithUserId as userId ->
                userId.UserId
            | _ ->
                null

        new () = UserMessageExtractor(30)

    type UserGenerator () =
        static member FirstNames = [|
            "Yoda"; "Obi-Wan"; "Darth Vader"; "Leia"; "Luke"; "R2D2"; "Han"; 
            "Chewbacca"; "Jabba"; "Ardbeg"; "Lando"
            |]

        static member LastNames =  [|
            "Fat"; "Kenobi"; "Skywalker"; "Solo"; "Fett"; "Calrissian" 
            |]
    
        static member PickRandom<'T>(items: 'T array) = 
            items[ThreadLocalRandom.Current.Next(items.Length)]

        static member CreateRandom() : UserDescriptor =
    
            let prf = UserGenerator.PickRandom(UserGenerator.FirstNames)
            let prl = UserGenerator.PickRandom(UserGenerator.LastNames)
            let userName = $"{prf} {prl} v{ThreadLocalRandom.Current.Next(0,100)}"
            let userId = MurmurHash.StringHash(userName)
            printfn "userId: %d" userId
            new UserDescriptor(userId.ToString(), userName)



    type TestDataPopulatorService (system:ActorSystem) =

        let _system = system
        let mutable _cancelable = Unchecked.defaultof<ICancelable>

        interface IHostedService with


            override this.StartAsync(cancellationToken:CancellationToken) =
    
                _cancelable <- _system.Scheduler.Advanced.ScheduleRepeatedlyCancelable(
                    TimeSpan.Zero
                    , TimeSpan.FromSeconds(1)
                    , fun _ ->
                        let entityRegion = ActorRegistry.For(_system).Get<UserActionsEntity>()
                        let user = UserGenerator.CreateRandom()
                        entityRegion.Tell(new CreateUser(user))
                    )

                Task.CompletedTask

            override this.StopAsync(cancellationToken:CancellationToken) =
                _cancelable.Cancel()
                Task.CompletedTask



open Akka.Actor
open Akka.Cluster.Hosting
open Akka.Cluster.Sharding
open Akka.Hosting
open CustomJournalIdDemo
open Actors
open Messages
open Akka.Persistence.SqlServer.Hosting
open Akka.Remote.Hosting

module Main =
    open Microsoft.AspNetCore.Builder
    open Microsoft.Extensions.DependencyInjection
    open System
    open Microsoft.AspNetCore.Routing
    open Microsoft.AspNetCore.Http
    open System.Threading.Tasks
    open System.Collections.Generic
    type RootHandler = delegate of ActorRegistry -> Task<IEnumerable<UserDescriptor>>
    type UserHandler = delegate of string * ActorRegistry -> Task<UserDescriptor>


    [<EntryPoint>]
    let main argv =
        let builder = WebApplication.CreateBuilder(argv)
        
        let akkaFun =
            Action<_>(fun (configurationBuilder: AkkaConfigurationBuilder) ->
   
            // Grab connection strings from appsettings.json
                let localConn = //builder.Configuration.GetConnectionString("sqlServerLocal");
                    "sqlServerLocal"
                let shardingConn = //builder.Configuration.GetConnectionString("sqlServerSharding");
                    "sqlServerSharding"
            // Custom journal options with the id "sharding"
            // The absolute id will be "akka.persistence.journal.sharding"
                let shardingJournalOptions = 
                    new SqlServerJournalOptions(
                        isDefaultPlugin = false
                        , Identifier = "sharding"
                        )
                shardingJournalOptions.ConnectionString <- shardingConn
                shardingJournalOptions.AutoInitialize <- true
        
            // Custom snapshots options with the id "sharding"
            // The absolute id will be "akka.persistence.snapshot-store.sharding"
                let shardingSnapshotOptions = 
                    new SqlServerSnapshotOptions(
                        isDefaultPlugin = false
                        , Identifier = "sharding"
                        )
                shardingSnapshotOptions.ConnectionString <- shardingConn
                shardingSnapshotOptions.AutoInitialize <- true
                let co = new ClusterOptions()
                co.Roles <- [| "myRole" |]
                co.SeedNodes <- [| "akka.tcp://MyActorSystem@localhost:8110" |]
                let so = new ShardOptions ()
                so.StateStoreMode <- StateStoreMode.Persistence
                so.Role <- "myRole"
                so.JournalPluginId <- shardingJournalOptions.PluginId
                so.SnapshotPluginId <- shardingSnapshotOptions.PluginId

                let actorsFun = 
                    Action<_, _>(
                        fun (system:ActorSystem) (registry:IActorRegistry) ->            
                            let userActionsShard = registry.Get<UserActionsEntity>()
                            let indexer = system.ActorOf(Props.Create(fun _ -> new Indexer(userActionsShard)), "index")
                            registry.TryRegister<Index>(indexer) |> ignore
                        )


                let cb = 
                    configurationBuilder
                        .WithRemoting("localhost", 8110)
                        .WithClustering(co)
                        .WithSqlServerPersistence(localConn) // Standard way to create a default persistence journal and snapshot
                        .WithSqlServerPersistence(shardingJournalOptions, shardingSnapshotOptions) // This is a custom persistence setup using the options instances we've set up earlier
                        .WithShardRegion<UserActionsEntity>(
                            "userActions", fun s -> UserActionsEntity.Props(s)
                            , new UserMessageExtractor()
                            , so)
                        .WithActors(actorsFun)
                cb |> ignore
                )
                
        builder.Services
            .AddAkka("FAkkaHttp", akkaFun)
            .AddHostedService<TestDataPopulatorService>() |> ignore

        let app = builder.Build()

        let rootHandler =
            RootHandler(fun (registry:ActorRegistry) ->
                task {
                    let index = registry.Get<Index>()
                    let! ca =
                        index.Ask<IEnumerable<UserDescriptor>>(FetchUsers.Instance, TimeSpan.FromSeconds(3))
                            .ConfigureAwait(false)
                    return ca
                })

        let userHandler = 
            UserHandler(fun (userId:string) (registry:ActorRegistry) ->
                task {
                    let index = registry.Get<UserActionsEntity>();
                    return! index.Ask<UserDescriptor>(new FetchUser(userId), TimeSpan.FromSeconds(3))
                        .ConfigureAwait(false)
                })
            
        

        app.MapGet("/", rootHandler) |> ignore

        app.MapGet("/user/{userId}", userHandler) |> ignore

        app.Run()
        0

