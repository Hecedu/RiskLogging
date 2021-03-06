using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Risk.Game;
using Microsoft.Extensions.Configuration;
using Risk.Shared;
using System.Threading;
using Serilog;
using System.Diagnostics;

namespace Risk.Server.Hubs
{
    public class RiskHub : Hub<IRiskHub>
    {
        private readonly ILogger<RiskHub> logger;
        private readonly IConfiguration config;
        public const int MaxFailedTries = 5;
        public const int TimeoutInSeconds = 2;
        Stopwatch responsetime = new Stopwatch();
        private Player currentPlayer => (game.CurrentPlayer as Player);
        private Risk.Game.Game game { get; set; }

        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        public RiskHub(ILogger<RiskHub> logger, IConfiguration config, Game.Game game)
        {
            this.logger = logger;
            this.config = config;
            this.game = game;
        }

        public override async Task OnConnectedAsync()
        {
            Log.Information("Player with connection ID: " + Context.ConnectionId + " has connected.");
            logger.LogInformation(Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            Log.Information("Player with connection ID: " + Context.ConnectionId + ". Exception received: " + exception.Message);
            var player = game.RemovePlayerByToken(Context.ConnectionId);
            await BroadCastMessageAsync($"Player {player.Name} disconnected.  Removed from game.");
            await base.OnDisconnectedAsync(exception);
        }

        public async Task SendMessage(string user, string message)
        {
            Log.Information("Message: " + message + " sent to player: " + user + ".");
            await Clients.All.SendMessage(user, message);
        }

        public async Task Signup(string user)
        {
            Log.Information("user: " + user + " is attempting to log.");
            var duplicatePlayer = game.Players.ToList().FirstOrDefault(player => player.Token == Context.ConnectionId);
            if(duplicatePlayer != null)
            {
                Log.Information("Log in attempt by user " + user + $" failed. There is already a player registered on your client named {duplicatePlayer.Name}");
                await Clients.Client(duplicatePlayer.Token).SendMessage("Server", $"There is already a player registered on your client named {duplicatePlayer.Name}");
                (duplicatePlayer as Player).Strikes++;
            }
            else if(game.GameState == GameState.Deploying || game.GameState == GameState.Attacking)
            {
                Log.Information("Log in attempt by user " + user + " failed. There's already a game in progress.  Disconnect then try again once the game has finished.");
                await Clients.Client(Context.ConnectionId).SendMessage("Server", "There's already a game in progress.  Disconnect then try again once the game has finished.");
            }
            else
            {
                int i = 1;
                var baseName = user;
                while (game.Players.Any(p => p.Name == user))
                {
                    user = string.Concat(baseName, i.ToString());
                    i++;
                }
                logger.LogInformation($"{Context.ConnectionId}: {user}");
                var newPlayer = new Player(Context.ConnectionId, user);
                game.AddPlayer(newPlayer);
                Log.Information("User: " + baseName + " has joined the name as: " + user + ".");
                await BroadCastMessageAsync(newPlayer.Name + " has joined the game");
                await Clients.Client(newPlayer.Token).SendMessage("Server", "Welcome to the game " + newPlayer.Name);
                await Clients.Client(newPlayer.Token).JoinConfirmation(newPlayer.Name);
            }
        }

        private async Task BroadCastMessageAsync(string message)
        {
            Log.Information("Message sent to all clients: " + message);
            await Clients.All.SendMessage("Server", message);
        }

        private GameStatus getStatus()
        {
            GameStatus status = game.GetGameStatus();
            status.CurrentPlayer = currentPlayer.Name;
            return status;
        }

        public async Task GetStatus()
        {
            await Clients.Client(Context.ConnectionId).SendMessage("Server", game.GameState.ToString());            
            await Clients.Client(Context.ConnectionId).SendStatus(getStatus());
        }

        public async Task RestartGame(string password, GameStartOptions startOptions)
        {
            Stopwatch processingtime = new Stopwatch();
            processingtime.Reset();
            processingtime.Start();
            Log.Information("Restart Game called.");
            if(password == config["StartGameCode"])
            {
                if(game.Players.Count() == 0)
                {
                    Log.Warning("Couldnt restart game, no players connected.");
                    await BroadCastMessageAsync("No players connected.  Unable to restart.");
                    return;
                }
                
                await BroadCastMessageAsync("Restarting game...");
                game.RestartGame(startOptions);
                await StartDeployPhase();
                await Clients.All.SendStatus(getStatus());
                processingtime.Stop();
                Log.Information("Restart Game successfuly executed in: " + processingtime.Elapsed);
            }
            else
            {
                processingtime.Stop();
                Log.Warning("Couldnt Restart Game, wrong password.");
                await Clients.Client(Context.ConnectionId).SendMessage("Server", "Incorrect password.");
            }
        }

        public async Task StartGame(string Password)
        {
            Stopwatch processingtime = new Stopwatch();
            processingtime.Reset();
            processingtime.Start();
            Log.Information("Start Game called");
            if (Password == config["StartGameCode"])
            {
                await BroadCastMessageAsync("The Game has started");
                game.StartGame();
                await StartDeployPhase();
                processingtime.Stop();
                Log.Information("Start Game successfuly executed in : " + processingtime.Elapsed);
            }
            else
            {
                processingtime.Stop();
                Log.Warning("Couldnt Start Game, wrong password.");
                await Clients.Client(Context.ConnectionId).SendMessage("Server", "Incorrect password");
            }
        }

        private async Task StartDeployPhase()
        {
            Log.Information("Deploy phase has started.");
            game.CurrentPlayer = game.Players.First();

            await Clients.Client(currentPlayer.Token).YourTurnToDeploy(game.Board.SerializableTerritories);
        }

        public async Task DeployRequest(Location l)
        {
            Log.Information("deploy request received from player '" + currentPlayer.Name + "' To location: " + l.Column + "," + l.Row);
            if (game.GameState == GameState.GameOver)
                return;

            logger.LogInformation("Received DeployRequest from {connectionId}", Context.ConnectionId);

            if(Context.ConnectionId == currentPlayer.Token)
            {
                if(currentPlayer.Strikes >= MaxFailedTries)
                {
                    if(game.Players.Count() == 1)
                    {
                        await sendGameOverAsync();
                        return;
                    }
                    Log.Warning("Player: " + currentPlayer.Name + " kicked out due to too many bad deploy requests.");
                    logger.LogInformation("{0} has too many strikes.  Booting from game.", currentPlayer.Name);
                    await Clients.Client(Context.ConnectionId).SendMessage("Server", "Too many bad requests. No risk for you");
                    game.RemovePlayerByToken(currentPlayer.Token);
                    game.RemovePlayerFromBoard(currentPlayer.Token);
                    await tellNextPlayerToDeploy();
                    return;
                }

                if(game.TryPlaceArmy(Context.ConnectionId, l))
                {
                    await Clients.All.SendStatus(getStatus());
                    await Clients.Client(Context.ConnectionId).SendMessage("Server", $"Successfully Deployed At {l.Row}, {l.Column}");
                    logger.LogInformation("{currentPlayer} deployed at {l}", currentPlayer, l);

                    if(game.GameState == GameState.Deploying)
                    {
                        logger.LogInformation("Telling next player to deploy.");
                        await tellNextPlayerToDeploy();
                    }
                    else
                    {
                        logger.LogInformation("All armies that can be deployed have been deployed.  Beginning attack state.");
                        await StartAttackPhase();
                    }
                }
                else
                {
                    currentPlayer.Strikes++;
                    logger.LogInformation("{currentPlayer} tried to deploy at {l} but deploy failed.  Increasing strikes.  You now have {strikes} strikes!",
                        currentPlayer.Name, l, currentPlayer.Strikes);
                    await Clients.Client(Context.ConnectionId).SendMessage("Server", "Did not deploy successfully");
                    Log.Warning("Deploy response by player: " + currentPlayer.Name + " unsuccessful, sending new deploy request.");
                    await Clients.Client(currentPlayer.Token).YourTurnToDeploy(game.Board.SerializableTerritories);
                }
            }
            else
            {
                var badPlayer = game.Players.Single(p => p.Token == Context.ConnectionId) as Player;
                badPlayer.Strikes++;
                await Clients.Client(badPlayer.Token).SendMessage("Server", "It's not your turn");
                logger.LogInformation("{badPlayer} tried to deploy when it wasn't their turn.  Increasing invalid request count.  You now have {strikes} strikes!",
                    badPlayer.Name, badPlayer.Strikes);
            }
        }

        private async Task tellNextPlayerToDeploy()
        {
            if (game.GameState == GameState.GameOver)
                return;

            var players = game.Players.ToList();
            var currentPlayerIndex = players.IndexOf(game.CurrentPlayer);
            var nextPlayerIndex = currentPlayerIndex + 1;
            if (nextPlayerIndex >= players.Count)
            {
                nextPlayerIndex = 0;
            }

            if(players.Count <= nextPlayerIndex)
            {
                logger.LogWarning("What happened to all the players?!");
                await sendGameOverAsync();
                return;
            }

            game.CurrentPlayer = players[nextPlayerIndex];
            Log.Information("Asking player " + currentPlayer.Name + " to deploy.");
            await Clients.Client(currentPlayer.Token).YourTurnToDeploy(game.Board.SerializableTerritories);
        }

        private async Task StartAttackPhase()
        {
            Log.Information("Starting attack phase");
            game.CurrentPlayer = game.Players.First();

            await Clients.Client(currentPlayer.Token).YourTurnToAttack(game.Board.SerializableTerritories);
        }

        public async Task AttackRequest(Location from, Location to)
        {
            responsetime.Stop();
            Stopwatch processingtime = new Stopwatch();
            processingtime.Start();
            Log.Information("Attack request received from player " + currentPlayer.Name);
            Log.Information("Attack request by " + currentPlayer + " from location: " + from.Row + "," +from.Column);
            Log.Information("Attack request by " + currentPlayer + " to location: " + to.Row + "," + to.Column);

            if (game.GameState == GameState.GameOver)
                return;

            if (Context.ConnectionId == currentPlayer.Token)
            {
                game.OutstandingAttackRequestCount--;

                if (currentPlayer.Strikes >= MaxFailedTries)
                {
                    if (game.Players.Count() == 1)
                    {
                        await sendGameOverAsync();
                        return;
                    }
                    Log.Information("Player " + currentPlayer.Name + " kicked out for too many bad attack responses.");
                    await Clients.Client(Context.ConnectionId).SendMessage("Server", $"Too many bad requests. No risk for you");
                    game.RemovePlayerByToken(currentPlayer.Token);
                    game.RemovePlayerFromBoard(currentPlayer.Token);
                    await tellNextPlayerToAttack();
                    return;
                }

                if (game.Players.Count() > 1 && game.GameState == GameState.Attacking && game.Players.Any(p => game.PlayerCanAttack(p)))
                {
                    if (game.PlayerCanAttack(currentPlayer))
                    {
                        TryAttackResult attackResult = new TryAttackResult { AttackInvalid = false };
                        Territory attackingTerritory = null;
                        Territory defendingTerritory = null;
                        try
                        {
                            attackingTerritory = game.Board.GetTerritory(from);
                            defendingTerritory = game.Board.GetTerritory(to);

                            logger.LogInformation($"{currentPlayer.Name} wants to attack from {attackingTerritory} to {defendingTerritory}");

                            attackResult = game.TryAttack(currentPlayer.Token, attackingTerritory, defendingTerritory);
                            await Clients.All.SendStatus(getStatus());
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Attack exception caused by "+ currentPlayer.Name +": " + ex.Message);
                            attackResult = new TryAttackResult { AttackInvalid = true, Message = ex.Message };
                        }
                        if (attackResult.AttackInvalid)
                        {
                            currentPlayer.Strikes++;
                            logger.LogError($"Invalid attack request! {currentPlayer.Name} from {attackingTerritory} to {defendingTerritory}.  You now have {currentPlayer.Strikes} strike(s)!");
                            await Clients.Client(currentPlayer.Token).SendMessage("Server", $"Invalid attack request: {attackResult.Message} :(  You now have {currentPlayer.Strikes} strike(s)!");
                            await Clients.Client(currentPlayer.Token).YourTurnToAttack(game.Board.SerializableTerritories);
                        }
                        else
                        {
                            await Clients.Client(Context.ConnectionId).SendMessage("Server", $"Successfully Attacked From ({from.Row}, {from.Column}) To ({to.Row}, {to.Column})");

                            if (game.GameState == GameState.Attacking)
                            {
                                if (game.PlayerCanAttack(currentPlayer))
                                {
                                    await Clients.Client(currentPlayer.Token).YourTurnToAttack(game.Board.SerializableTerritories);
                                }
                                else
                                    await tellNextPlayerToAttack();
                            }
                            else
                            {
                                await sendGameOverAsync();
                            }
                        }
                    }
                    else
                    {
                        await Clients.Client(currentPlayer.Token).SendMessage("Server", "You are unable to attack.  Moving to next player.");
                        logger.LogInformation("Player {currentPlayer} cannot attack.", currentPlayer.Name);
                        await tellNextPlayerToAttack();
                    }
                }
                else
                {
                    await sendGameOverAsync();
                }
            }
            else
            {
                var badPlayer = game.Players.Single(p => p.Token == Context.ConnectionId) as Player;
                badPlayer.Strikes++;
                logger.LogInformation("Player {currentPlayer} tried to play when it's not their turn.  You now have {strikes} strikes!", badPlayer.Name, badPlayer.Strikes);
                processingtime.Stop();
                Log.Information("Processing attack request by "+ currentPlayer.Name + " took: " +processingtime.Elapsed);
                await Clients.Client(badPlayer.Token).SendMessage("Server", "It's not your turn");
            }
        }

        public async Task AttackComplete()
        {
            Log.Information("Attack complete");
            if (game.GameState == GameState.GameOver)
                return;

            if (game.Players.Count() > 1 && game.GameState == GameState.Attacking && game.Players.Any(p => game.PlayerCanAttack(p)))
                await tellNextPlayerToAttack();
            else
                await sendGameOverAsync();
        }

        private async Task tellNextPlayerToAttack()
        {
            responsetime.Restart();
            responsetime.Start();
            if (game.GameState == GameState.GameOver)
                return;

            var players = game.Players.ToList();
            if (game.OutstandingAttackRequestCount >= players.Count * Game.Game.MaxTimesAPlayerCanNotAttack)
            {
                logger.LogInformation("Too many players skipped attacking, ending game.");
                await sendGameOverAsync();
                return;
            }
            game.OutstandingAttackRequestCount++;
            var currentPlayerIndex = players.IndexOf(game.CurrentPlayer);
            var nextPlayerIndex = currentPlayerIndex + 1;
            if (nextPlayerIndex >= players.Count)
            {
                nextPlayerIndex = 0;
            }
            if (players.Count <= nextPlayerIndex)
            {
                logger.LogWarning("What happened to all the players?!");
                await sendGameOverAsync();
                return;
            }

            game.CurrentPlayer = players[nextPlayerIndex];
            Log.Information("Asking player " + currentPlayer.Name + " to attack.");
            await Clients.Client(currentPlayer.Token).YourTurnToAttack(game.Board.SerializableTerritories);
        }

        private async Task sendGameOverAsync()
        {
            Log.Information("Game has finished");
            game.SetGameOver();
            var status = getStatus();
            logger.LogInformation("Game Over.\n\n{gameStatus}", status);
            var winners = status.PlayerStats.Where(s => s.Score == status.PlayerStats.Max(s => s.Score)).Select(s => s.Name);
            await BroadCastMessageAsync($"Game Over - {string.Join(',', winners)} win{(winners.Count() > 1?"":"s")}!");
            await Clients.All.SendStatus(getStatus());
        }
    }
}
