﻿using System;
using System.Text;
using System.Net.Sockets;
using NetCoreServer;
using System.Text.Json;
using POGOProtos.Rpc;
using Google.Protobuf.Collections;
using System.Linq;
using PolygonStats.Models;
using PolygonStats.Configuration;
using System.Collections.Generic;

namespace PolygonStats
{
    class ClientSession : TcpSession
    {
        private string messageBuffer = "";
        private string accountName = null;
        private Session dbSession;

        public ClientSession(TcpServer server) : base(server) { }

        protected override void OnConnected()
        {
            Console.WriteLine($"Polygon TCP session with Id {Id} connected!");
        }

        protected override void OnDisconnected()
        {
            Console.WriteLine($"Polygon TCP session with Id {Id} disconnected!");

            // Add ent time to session
            if (ConfigurationManager.shared.config.mysqlSettings.enabled)
            {
                if(dbSession != null)
                {
                    dbSession.EndTime = DateTime.UtcNow;
                    MySQLConnectionManager.shared.GetContext().SaveChanges();
                }
            }

        }

        protected override void OnReceived(byte[] buffer, long offset, long size)
        {
            string currentMessage = Encoding.UTF8.GetString(buffer, (int)offset, (int)size);
            //Console.WriteLine($"Message: {currentMessage}");

            messageBuffer += currentMessage;
            var jsonStrings = messageBuffer.Split("\n", StringSplitOptions.RemoveEmptyEntries);
            foreach (string jsonString in jsonStrings)
            {
                if(!jsonString.StartsWith("{"))
                {
                    continue;
                }
                if (!jsonString.Equals(""))
                {
                    string trimedJsonString = jsonString.Trim('\r', '\n'); ;
                    try
                    {
                        messageBuffer = "";
                        MessageObject message = JsonSerializer.Deserialize<MessageObject>(trimedJsonString);
                        foreach (Payload payload in message.payloads)
                        {
                            if(payload.account_name == null || payload.account_name.Equals("null"))
                            {
                                continue;
                            }
                            if (this.accountName != payload.account_name)
                            {
                                this.accountName = payload.account_name;
                                getStatEntry();

                                if (ConfigurationManager.shared.config.mysqlSettings.enabled)
                                {
                                    MySQLContext context = MySQLConnectionManager.shared.GetContext();
                                    Account acc = context.Accounts.Where(a => a.Name == this.accountName).FirstOrDefault<Account>();
                                    if (acc == null)
                                    {
                                        acc = new Account();
                                        acc.Name = this.accountName;
                                        acc.HashedName = "";
                                        //TODO: Add hashed name
                                        //acc.HashedName =  this.accountName.get
                                        context.Accounts.Add(acc);
                                    }
                                    dbSession = new Session { StartTime = DateTime.UtcNow, LogEntrys = new List<LogEntry>() };
                                    acc.Sessions.Add(dbSession);
                                    context.SaveChanges();
                                }
                            }
                            handlePayload(payload);
                        }
                    }
                    catch (JsonException e)
                    {
                        messageBuffer = jsonString;
                        //Console.WriteLine(e.ToString());
                    }
                }
            }
        }

        private Stats getStatEntry()
        {
            return StatManager.sharedInstance.getEntry(accountName);
        }

        private void handlePayload(Payload payload)
        {
            switch (payload.getMethodType())
            {
                case Method.CatchPokemon:
                    CatchPokemonOutProto catchPokemonProto = CatchPokemonOutProto.Parser.ParseFrom(payload.getDate());
                    //Console.WriteLine($"Pokemon {catchPokemonProto.DisplayPokedexId.ToString("G")} Status: {catchPokemonProto.Status.ToString("G")}.");
                    ProcessCaughtPokemon(catchPokemonProto);
                    break;
                case Method.GymFeedPokemon:
                    GymFeedPokemonOutProto feedPokemonProto = GymFeedPokemonOutProto.Parser.ParseFrom(payload.getDate());
                    if (feedPokemonProto.Result == GymFeedPokemonOutProto.Types.Result.Success)
                    {
                        ProcessFeedBerry(payload.account_name, feedPokemonProto);
                    }
                    break;
                case Method.CompleteQuest:
                    CompleteQuestOutProto questProto = CompleteQuestOutProto.Parser.ParseFrom(payload.getDate());
                    if (questProto.Status == CompleteQuestOutProto.Types.Status.Success)
                    {
                        ProcessQuestRewards(payload.account_name, questProto.Quest.Quest.QuestRewards);
                    }
                    break;
                case Method.CompleteQuestStampCard:
                    CompleteQuestStampCardOutProto completeQuestStampCardProto = CompleteQuestStampCardOutProto.Parser.ParseFrom(payload.getDate());
                    if (completeQuestStampCardProto.Status == CompleteQuestStampCardOutProto.Types.Status.Success)
                    {
                        ProcessQuestRewards(payload.account_name, completeQuestStampCardProto.Reward);
                    }
                    break;
                case Method.GetHatchedEggs:
                    GetHatchedEggsOutProto getHatchedEggsProto = GetHatchedEggsOutProto.Parser.ParseFrom(payload.getDate());
                    if (getHatchedEggsProto.Success)
                    {
                        ProcessHatchedEggReward(payload.account_name, getHatchedEggsProto);
                    }
                    break;
                case Method.FortSearch:
                    FortSearchOutProto fortSearchProto = FortSearchOutProto.Parser.ParseFrom(payload.getDate());
                    if (fortSearchProto.Result == FortSearchOutProto.Types.Result.Success)
                    {
                        ProcessSpinnedFort(payload.account_name, fortSearchProto);
                    }
                    break;
                case Method.EvolvePokemon:
                    EvolvePokemonOutProto evolvePokemon = EvolvePokemonOutProto.Parser.ParseFrom(payload.getDate());
                    if(evolvePokemon.Result == EvolvePokemonOutProto.Types.Result.Success)
                    {
                        ProcessEvolvedPokemon(payload.account_name, evolvePokemon);
                    }
                    break;
                default:
                    //Console.WriteLine($"Account: {payload.account_name}");
                    //Console.WriteLine($"Type: {payload.getMethodType().ToString("G")}");
                    break;
            }
        }

        private void ProcessEvolvedPokemon(string account_name, EvolvePokemonOutProto evolvePokemon)
        {
            Stats entry = getStatEntry();
            entry.addXp(evolvePokemon.ExpAwarded);

            if (ConfigurationManager.shared.config.mysqlSettings.enabled)
            {
                MySQLConnectionManager.shared.AddEvolvePokemonToDatabase(dbSession, evolvePokemon);
            }
        }

        private void ProcessFeedBerry(string account_name, GymFeedPokemonOutProto feedPokemonProto)
        {
            Stats entry = getStatEntry();
            entry.addXp(feedPokemonProto.XpAwarded);
            entry.addStardust(feedPokemonProto.StardustAwarded);

            if (ConfigurationManager.shared.config.mysqlSettings.enabled)
            {
                MySQLConnectionManager.shared.AddFeedBerryToDatabase(dbSession, feedPokemonProto);
            }
        }

        private void ProcessSpinnedFort(string account_name, FortSearchOutProto fortSearchProto)
        {
            Stats entry = getStatEntry();
            entry.addSpinnedPokestop();
            entry.addXp(fortSearchProto.XpAwarded);

            if (ConfigurationManager.shared.config.mysqlSettings.enabled)
            {
                MySQLConnectionManager.shared.AddSpinnedFortToDatabase(dbSession, fortSearchProto);
            }
        }

        protected override void OnError(SocketError error)
        {
            Console.WriteLine($"Chat TCP session caught an error with code {error}");
        }

        private void ProcessQuestRewards(string acc, RepeatedField<QuestRewardProto> rewards)
        {
            Stats entry = getStatEntry();
            foreach (QuestRewardProto reward in rewards)
            {
                if (reward.RewardCase == QuestRewardProto.RewardOneofCase.Exp)
                {
                    entry.addXp(reward.Exp);
                }
                if (reward.RewardCase == QuestRewardProto.RewardOneofCase.Stardust)
                {
                    entry.addStardust(reward.Stardust);
                }
            }

            if (ConfigurationManager.shared.config.mysqlSettings.enabled)
            {
                MySQLConnectionManager.shared.AddQuestToDatabase(dbSession, rewards);
            }
        }
        private void ProcessHatchedEggReward(string acc, GetHatchedEggsOutProto getHatchedEggsProto)
        {
            if (getHatchedEggsProto.HatchedPokemon.Count <= 0)
            {
                return;
            }
            Stats entry = getStatEntry();

            entry.addXp(getHatchedEggsProto.ExpAwarded.Sum());
            entry.addStardust(getHatchedEggsProto.StardustAwarded.Sum());

            if (ConfigurationManager.shared.config.mysqlSettings.enabled)
            {
                MySQLConnectionManager.shared.AddHatchedEggToDatabase(dbSession, getHatchedEggsProto);
            }
        }

        public void ProcessCaughtPokemon(CatchPokemonOutProto catchedPokemon)
        {
            Stats entry = getStatEntry();
            switch (catchedPokemon.Status)
            {
                case CatchPokemonOutProto.Types.Status.CatchSuccess:
                    entry.caughtPokemon++;
                    if (catchedPokemon.PokemonDisplay != null && catchedPokemon.PokemonDisplay.Shiny)
                    {
                        entry.shinyPokemon++;
                    }

                    entry.addXp(catchedPokemon.Scores.Exp.Sum());
                    entry.addStardust(catchedPokemon.Scores.Stardust.Sum());

                    if (ConfigurationManager.shared.config.mysqlSettings.enabled)
                    {
                        MySQLConnectionManager.shared.AddPokemonToDatabase(dbSession, catchedPokemon);
                    }
                    break;
                case CatchPokemonOutProto.Types.Status.CatchFlee:
                    entry.addXp(catchedPokemon.Scores.Exp.Sum());
                    entry.fleetPokemon++;

                    if (ConfigurationManager.shared.config.mysqlSettings.enabled)
                    {
                        MySQLConnectionManager.shared.AddPokemonToDatabase(dbSession, catchedPokemon);
                    }
                    break;
            }
        }
    }
}
