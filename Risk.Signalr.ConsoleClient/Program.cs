using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Risk.Shared;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Serilog;
using System.IO;

namespace Risk.Signalr.ConsoleClient
{
    static class Program
    {
        static HubConnection hubConnection;
        private static IPlayerLogic playerLogic;
        private static IConfiguration config;

        static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args);

        static async Task Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
              .SetBasePath(Directory.GetCurrentDirectory())
              .AddJsonFile("appsettings.json")
              .Build();
            Log.Logger = new LoggerConfiguration()
                .ReadFrom.Configuration(configuration)
                .CreateLogger();
            if (args.Any(s=>s == "?" || s == "help" ))
            {
                Console.WriteLine("Valid arguments:");
                Console.WriteLine("\t--playerName joe");
                Console.WriteLine("\t--serverAddress http://localhost:5000");
                Console.WriteLine("\t--useAlternate true");
                Console.WriteLine("\t--sleep random");
                return;
            }

            using IHost host = CreateHostBuilder(args).Build();
            config = host.Services.GetService(typeof(IConfiguration)) as IConfiguration ?? throw new Exception("Unable to load configuration");            

            Console.WriteLine("What is your player name?");
            var playerName = config["playerName"] switch
            {
                null => Console.ReadLine(),
                string name => name
            };
            Console.WriteLine("Hello {0}!", playerName);


            var serverAddress = config["serverAddress"] ?? "http://localhost:5000";
            Console.WriteLine($"Talking to the server at {serverAddress}");
            Log.Information("Connected to server at: " + serverAddress);

            hubConnection = new HubConnectionBuilder()
                .WithUrl($"{serverAddress}/riskhub")
                .Build();

            hubConnection.On<IEnumerable<BoardTerritory>>(MessageTypes.YourTurnToDeploy, async (board) =>
            {
                Log.Information("Deploy request received");
                var deployLocation = playerLogic.WhereDoYouWantToDeploy(board);
                Console.WriteLine("Deploying to {0}", deployLocation);
                Log.Information("Deploy response sent to" + deployLocation.Column + "," + deployLocation.Row);
                await DeployAsync(deployLocation);
            });

            hubConnection.On<IEnumerable<BoardTerritory>>(MessageTypes.YourTurnToAttack, async (board) =>
            {
                Log.Information("Attack response received.");
                try
                {
                    (var from, var to) = playerLogic.WhereDoYouWantToAttack(board);

                    Console.WriteLine("Attacking from {0} ({1}) to {2} ({3})", from, board.First(c => c.Location == from).OwnerName, to, board.First(c => c.Location == to).OwnerName);
                    Log.Information("Attack response successful from (" + from.Column + "," + from.Row + ") to (" + to.Column + "," + to.Row + ").");
                    await AttackAsync(from, to);
                }
                catch (Exception e)
                {
                    Log.Error("Exception caught: " + e.Message);
                    Console.WriteLine("Yielding turn (nowhere left to attack)");
                    await AttackCompleteAsync();
                }
            });

            hubConnection.On<string, string>(MessageTypes.SendMessage, (from, message) =>
            {
                Console.WriteLine("From {0}: {1}", from, message);
                Log.Information("Message received from " + from + ": " + message);
            });

            hubConnection.On<string>(MessageTypes.JoinConfirmation, validatedName => 
            {
                if (config["useAlternate"] == "true")
                {
                    playerLogic = new AlternateSampleLogic(validatedName);
                }
                else
                {
                    var shouldSleep = config["sleep"] == "random";
                    playerLogic = new PlayerLogic(validatedName, shouldSleep);
                }
                Console.Title = validatedName;
                Console.WriteLine($"Successfully joined server. Assigned Name is {validatedName}");
                Log.Information("Joining game as: " + validatedName);
            });

            await hubConnection.StartAsync();

            Console.WriteLine("My connection id is {0}.  Waiting for game to start...", hubConnection.ConnectionId);
            await SignupAsync(playerName);

            Console.ReadLine();

            Console.WriteLine("Disconnecting from server.  Game over.");
        }

        static async Task SignupAsync(string playerName)
        {
            await hubConnection.SendAsync(MessageTypes.Signup, playerName);
        }

        static async Task DeployAsync(Location desiredLocation)
            => await hubConnection.SendAsync(MessageTypes.DeployRequest, desiredLocation);

        static async Task AttackAsync(Location from, Location to)
            => await hubConnection.SendAsync(MessageTypes.AttackRequest, from, to);

        static async Task AttackCompleteAsync()
            => await hubConnection.SendAsync(MessageTypes.AttackComplete);
    }
}
