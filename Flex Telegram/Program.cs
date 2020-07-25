using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Data;
using System.Data.SQLite;
using System.Security.Cryptography;

using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

using EDF_API;
using SQLiteDatabase;
using EncryptString;

using Newtonsoft.Json;
using System.Threading;
using Telegram.Bot.Exceptions;

namespace Flex_Telegram
{
    class Program
    {
        private static readonly long adminId;    // My telegram ID - value removed for repo
        private static readonly string token;    // Telegram access token - value removed for repo
        private static readonly string testToken;    // Telegram access token for testing - value removed for repo
        private static readonly TelegramBotClient Bot = new TelegramBotClient(token);    // Bot instance

        private static Dictionary<long, EDFSession> currentSessions = new Dictionary<long, EDFSession>();    // Hold active browser sessions
        private static Dictionary<long, string> currentCommands = new Dictionary<long, string>();    // Hold status of active commands
        private static Dictionary<long, System.Timers.Timer> commandTimers = new Dictionary<long, System.Timers.Timer>();    // Hold timers for each command
        private static readonly Dictionary<string, Func<int, Dictionary<string, string>, bool>> commandDict = new Dictionary<string, Func<int, Dictionary<string, string>, bool>>
        {
            { "start", StartCommand },
            { "singlesess", SingleSessionSelectCommand },
            { "prefs", PrefsCommand },
            { "tomorrow", TomorrowCommand },
            { "settings", SettingsCommand },
            { "today", GetTodayCommand }
        };    //Associate each command function with a string identifier

        private static readonly int cmdTimeout = 180;    // How before a command times out and is removed from currentCommands

        private static string curDir;    // Working directory of the executable
        private static SQLite userdb;    // SQLite database of users

        private static bool updateBool = false;    // Is the bot about to be stopped for updates  

        static void Main(string[] args)
        {
            // Get the working directory and the user database
            curDir = Directory.GetCurrentDirectory();
            userdb = new SQLite(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\NestBots\users.db");

            // Get the bot's name and set the console title to it
            var me = Bot.GetMeAsync().Result;
            Console.Title = me.Username;
            
            // Set BotOnMessageReceived to be called when the bot receives a telegram message.  Same thing with BotOnCallbackQueryReceived for callbaback queries.
            Bot.OnMessage += BotOnMessageReceived;
            Bot.OnCallbackQuery += BotOnCallbackQueryReceived;

            // Start receiving updates.  Enter any input to stop bot.
            Bot.StartReceiving(Array.Empty<UpdateType>());
            Console.WriteLine($"Start listening for {me.Username}");
            Console.ReadLine();
            Bot.StopReceiving();
        }

        // Initial user setup (/start)
        private static bool StartCommand(int stage, Dictionary<string, string> args)
        {
            // Deserialize the telegram message from json string into Message
            var msg = JsonConvert.DeserializeObject<Telegram.Bot.Types.Message>(args["msg"]);
            
            // Get data table of users already in the database with the same telegram ID as the person who sent the message
            // We can check if this person has already started the setup process in a different session that timed out
            var knownUserDt = userdb.ExecuteQuery($"select * from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';");

            if (stage == 0)    // Make sure person just started the command.  We don't want users who are in the middle of the command.
            {
                if (knownUserDt.Rows.Count > 0)    // First make sure the data table isn't empty
                    // If the password field isn't null, then this person has already gone through the setup process and doesn't need to continue with the command
                    // Otherwise, this person is in the middle of setup and should be on stage 2
                    if (knownUserDt.Rows[0]["password"].GetType() != typeof(DBNull))
                    {
                        currentCommands.Remove(msg.Chat.Id);
                        return true;
                    }
                    else
                        stage += 2;
            }

            string cmdStr = "start";    // The string identifier for this command

            switch(stage)
            {
                // Prompt user for first and last name
                case 0:
                    Bot.SendTextMessageAsync(msg.Chat.Id,
                        "Welcome to Flex Bot!  In order to work, I'll need your school email and password.  This info will only be used to login into the Flex Scheduling System." +
                        "\n\nEnter the first part of your email (before the @):");
                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";    // Save command status by telegram ID.  Stage is incremented by 1.
                    break;

                // Extract first and last name and use them to get the user's email
                case 1:
                    var name = msg.Text.ToLower();

                    // Make sure there aren't unwanted characters just in case the user did input their full email address
                    if (name.Contains("@"))
                        name = name.Substring(0, name.IndexOf('@'));

                    // Create email address and json string of template for teacher preferences
                    var email = name + "@depere.k12.wi.us";
                    var prefsJson = JsonConvert.SerializeObject(new Dictionary<string, string>() { { "default", "" }, { "tue", "default" }, { "wed", "default" }, { "thu", "default" }, { "fri", "default" } });

                    // Insert data into user database
                    if (userdb.ExecuteQuery($"select * from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';").Rows.Count == 0)
                        userdb.ExecuteNonQuery($"insert into user_accounts (email, prefs, telegram_id, auto_on) values (@EMAIL, '{prefsJson}', '{Convert.ToString(msg.Chat.Id)}', '0');", 
                            new Dictionary<SQLiteParameter, object> { { new SQLiteParameter("@EMAIL", DbType.String), email } });
                    else
                        userdb.ExecuteNonQuery($"update user_accounts set email = @EMAIL where telegram_id = '{Convert.ToString(msg.Chat.Id)}';",
                            new Dictionary<SQLiteParameter, object> { { new SQLiteParameter("@EMAIL", DbType.String), email } });

                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";
                    StartCommand(stage, args);
                    break;

                // Prompt user for password
                case 2:
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Enter password:");
                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";
                    break;

                // Test username and password.  If they work, user is all set to use the bot.
                case 3:
                    // Get password from message text and retrieve email from database
                    var pass = msg.Text;
                    var storedEmail = (string)userdb.ExecuteQuery($"select * from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';").Rows[0]["email"];

                    Bot.SendTextMessageAsync(msg.Chat.Id, "Trying login info.  This will take a few seconds...");

                    // Create a new EDFSession and try to sign in
                    var edf = new EDFSession(storedEmail, pass);
                    var canSignIn = edf.SignIn();
                    edf.Close();

                    // If the username and password don't work, ask for them again
                    if (!canSignIn)
                    {
                        Bot.SendTextMessageAsync(msg.Chat.Id, "Login failed.  Name or password is incorrect");
                        Thread.Sleep(5);
                        Bot.SendTextMessageAsync(msg.Chat.Id, @"Enter the first part of your email (before the @):", parseMode: ParseMode.Html);
                        currentCommands[msg.Chat.Id] = $"{cmdStr}-1";
                        break;
                    }

                    // Encrypt password
                    RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
                    byte[] buff = new byte[5];
                    rng.GetBytes(buff);
                    var salt = Convert.ToBase64String(buff);

                    pass = StringCipher.Encrypt(pass, salt);
                    
                    // Update database entry with password
                    userdb.ExecuteNonQuery($"update user_accounts set password = \"{pass}\", password_salt = \"{salt}\" where telegram_id = '{Convert.ToString(msg.Chat.Id)}';");

                    // Info on how to use the bot
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Login successful.  You can delete any messages of your password.").Wait();
                    Thread.Sleep(1);
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Flex Bot is ready to use!").Wait();
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Use /setnext to sign up for the next flex session").Wait();
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Use /prefs to set a schedule for auto sign up.").Wait();
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Auto sign up can be turned on in /settings.").Wait();
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Also use /help to view all available commands.\n" +
                        "Check out https://t.me/flexupdates for updates on this bot.  Enjoy!").Wait();

                    currentCommands.Remove(msg.Chat.Id);
                    break;
                default:
                    break;
            }

            return true;
        }

        // Create new EDFSession and sign in
        private static bool Signin(Dictionary<string, string> args)
        {
            // Deserialize message and get the email and password from the database
            var msg = JsonConvert.DeserializeObject<Telegram.Bot.Types.Message>(args["msg"]);
            var userDt = userdb.ExecuteQuery($"select * from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';");
            var email = (string)userDt.Rows[0]["email"];
            var passCipher = (string)userDt.Rows[0]["password"];
            var salt = (string)userDt.Rows[0]["password_salt"];
            var passClear = StringCipher.Decrypt(passCipher, salt);

            // Create new EDFSession and add it to currentSessions
            var edf = new EDFSession(email, passClear);
            currentSessions.Add(msg.Chat.Id, edf);

            // When the the session times out, close the session, remove it from currentSessions, and remove active command if there is one
            edf.SetTimeout((s, e) =>
            {
                edf.Close();
                currentSessions.Remove(msg.Chat.Id);
                if (currentCommands.ContainsKey(msg.Chat.Id))
                {
                    currentCommands.Remove(msg.Chat.Id);
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Command timed out.", replyMarkup: new ReplyKeyboardRemove());
                }
            });

            bool isSignedIn = edf.SignIn();
            if (!isSignedIn)
            {
                edf.Close();
                currentSessions.Remove(msg.Chat.Id);
            }

            return isSignedIn;
        }

        // Set the next day's Flex (/setnext)
        private static bool SingleSessionSelectCommand(int stage, Dictionary<string, string> args)
        {
            // Deserialize the message and store the command string identifier
            var msg = JsonConvert.DeserializeObject<Telegram.Bot.Types.Message>(args["msg"]);
            string cmdStr = "singlesess";

            switch (stage)
            {
                // Signin and retrieve Flex options
                case 0:
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Retrieving session list...", replyMarkup: new ReplyKeyboardRemove());

                    // If there isn't an EDFSession already up, start a new one
                    if (!currentSessions.ContainsKey(msg.Chat.Id))
                    {
                        bool isSignedIn = Signin(args);
                        if (!isSignedIn)
                        {
                            currentCommands.Remove(msg.Chat.Id);
                            Bot.SendTextMessageAsync(msg.Chat.Id, "Login failed.");
                            return false;
                        }
                    }
                    // Get list of available Flex sessions
                    var sess = currentSessions[msg.Chat.Id].GetAvailableSessions();

                    // Generate list of options for Telegram's ReplyKeyboard
                    var keys = new List<string[]>();
                    keys.Add(new string[] { "Cancel" });
                    foreach (var k in sess.Keys)
                        keys.Add(new string[] { k });

                    var sessDate = currentSessions[msg.Chat.Id].GetDate();
                    sessDate = sessDate.Substring(0, sessDate.Length - 6);

                    ReplyKeyboardMarkup ReplyKeyboard = keys.ToArray();
                    Bot.SendTextMessageAsync(msg.Chat.Id, $"Choose a Flex session for {sessDate}", replyMarkup: ReplyKeyboard);

                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";
                    break;

                // Set next Flex to selected option
                case 1:
                    if (msg.Text == "Cancel")
                        Bot.SendTextMessageAsync(msg.Chat.Id, "Cancelled", replyMarkup: new ReplyKeyboardRemove());
                    else
                    {
                        var edf = currentSessions[msg.Chat.Id];
                        edf.SelectSession(msg.Text);
                        edf.AcceptAlert(true);    // If there's a session already set for the next day, an alert will pop up asking if it should be overrided (which we want)
                        var sessInfo = edf.GetSessionTomorrow();
                        Bot.SendTextMessageAsync(msg.Chat.Id, $"Session selected\n\n{sessInfo}", replyMarkup: new ReplyKeyboardRemove());
                    }
                    
                    currentCommands.Remove(msg.Chat.Id);
                    break;
                default:
                    break;
            }
            return true;
        }

        // Edit teacher preferences for Flex schedule (/prefs)
        private static bool PrefsCommand(int stage, Dictionary<string, string> args)
        {
            // Deserialize the message; declare variables callback, inlineKeyboard, and replyKeyboard for later use; and set the command string identifier
            var msg = JsonConvert.DeserializeObject<Telegram.Bot.Types.Message>(args["msg"]);
            Telegram.Bot.Types.CallbackQuery callback;
            string cmdStr = "prefs";

            InlineKeyboardMarkup inlineKeyboard;
            ReplyKeyboardMarkup replyKeyboard;

            switch(stage)
            {
                // Construct an InlineKeyboard of options and send it
                case 0:
                    // Get the user's current default teacher
                    var prefsJson = (string)userdb.ExecuteQuery($"select prefs from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';").Rows[0]["prefs"];
                    var prefsDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(prefsJson);

                    var defaultSess = prefsDict["default"];
                    if (defaultSess == "")
                        defaultSess = "None";

                    // Create InlineKeyboard and send message
                    inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData($"Default-{defaultSess}", "2")
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("Schedule", "4")
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("Done")
                        }
                    });

                    var inlineMsg = Bot.SendTextMessageAsync(msg.Chat.Id, "Change default session or edit weekly schedule", replyMarkup: inlineKeyboard).Result;
                    // InlineKeyboards stay unless deleted by the bot or user.  A timeout is used in case the user doesn't click "Done" to finish the command.
                    createCommandTimer(inlineMsg);

                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";
                    break;

                // Move to a stage depending on which option was selected
                case 1:
                    // Only continue if args contain a callback.  If the user replies with a message instead of selecting an option, there won't be a callback.
                    if (!args.ContainsKey("callback"))
                        break;
                    // Deserialize the callback
                    callback = JsonConvert.DeserializeObject<Telegram.Bot.Types.CallbackQuery>(args["callback"]);
                    // Can temporarily remove timer while the bot is working on stuff.  Control is out of the user's hands.
                    removeCommandTimer(msg.Chat.Id);

                    // Stop command if user is done
                    if (callback.Data == "Done")
                    {
                        Bot.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
                        currentCommands.Remove(msg.Chat.Id);
                        break;
                    }

                    // Jump to stage associated with option selected
                    PrefsCommand(Convert.ToInt32(callback.Data), args);
                    break;

                // Let user pick new default teacher from stored list
                case 2:
                    using (FileStream file = File.Open(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\NestBots\teachers.json", FileMode.Open))
                    {
                        // Read teacher names into array
                        byte[] buffer = new byte[(int)file.Length];
                        file.Read(buffer, 0, (int)file.Length);
                        string[] teachersArray = JsonConvert.DeserializeObject<string[]>(Encoding.UTF8.GetString(buffer));

                        // Create ReplyKeyboard
                        var keyboardOptions = new List<string[]>();
                        keyboardOptions.Add(new string[] { "Cancel" });
                        foreach (var k in teachersArray)
                            keyboardOptions.Add(new string[] { k });

                        replyKeyboard = keyboardOptions.ToArray();
                    }

                    // Send message and start timer
                    Bot.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
                    var replyMsg = Bot.SendTextMessageAsync(msg.Chat.Id, "Choose a new default session", replyMarkup: replyKeyboard).Result;
                    createCommandTimer(replyMsg);

                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";
                    break;

                // Save new default teacher to database
                case 3:
                    removeCommandTimer(msg.Chat.Id);

                    if (msg.Text == "Cancel")
                        Bot.SendTextMessageAsync(msg.Chat.Id, "Cancelled", replyMarkup: new ReplyKeyboardRemove()).Wait();
                    else
                    {
                        UpdatePrefs(new Dictionary<string, string>() { { "default", msg.Text } }, msg.Chat.Id);
                        Bot.SendTextMessageAsync(msg.Chat.Id, $"{msg.Text} set for default session", replyMarkup: new ReplyKeyboardRemove()).Wait();
                    }
                    
                    PrefsCommand(0, args);
                    break;

                // Show current Flex schedule
                case 4:
                    prefsJson = (string)userdb.ExecuteQuery($"select prefs from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';").Rows[0]["prefs"];
                    prefsDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(prefsJson);

                    inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData($"Tuesday-{prefsDict["tue"]}")
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData($"Wednesday-{prefsDict["wed"]}")
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData($"Thursday-{prefsDict["thu"]}")
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData($"Friday-{prefsDict["fri"]}")
                        },
                        new []
                        {
                            InlineKeyboardButton.WithCallbackData("Back")
                        }
                    });

                    // Delete old message of options to replace it with a new message with the new options
                    if (msg.From.Id == Bot.BotId)
                        Bot.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);

                    inlineMsg = Bot.SendTextMessageAsync(msg.Chat.Id, "Choose a day to edit", replyMarkup: inlineKeyboard).Result;
                    createCommandTimer(inlineMsg);
                    
                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";
                    break;

                // Let user pick new teacher from stored list for selected day
                case 5:
                    callback = JsonConvert.DeserializeObject<Telegram.Bot.Types.CallbackQuery>(args["callback"]);
                    Bot.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
                    removeCommandTimer(msg.Chat.Id);

                    if (callback.Data == "Back")
                    {
                        PrefsCommand(0, args);
                        break;
                    }

                    using (FileStream file = File.Open(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\NestBots\teachers.json", FileMode.Open))
                    {
                        byte[] buffer = new byte[(int)file.Length];
                        file.Read(buffer, 0, (int)file.Length);
                        string[] teachersArray = JsonConvert.DeserializeObject<string[]>(Encoding.UTF8.GetString(buffer));

                        var keyboardOptions = new List<string[]>();
                        keyboardOptions.Add(new string[] { "Cancel" });
                        keyboardOptions.Add(new string[] { "default" });
                        foreach (var k in teachersArray)
                            keyboardOptions.Add(new string[] { k });

                        replyKeyboard = keyboardOptions.ToArray();
                    }
                    
                    replyMsg = Bot.SendTextMessageAsync(msg.Chat.Id, "Choose a new session", replyMarkup: replyKeyboard).Result;
                    createCommandTimer(replyMsg);

                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}-{callback.Data}";
                    break;

                // Save new teacher to database
                case 6:
                    removeCommandTimer(msg.Chat.Id);

                    var day = currentCommands[msg.Chat.Id].Split('-')[2];
                    var dayKey = day.ToLower().Substring(0, 3);

                    if (msg.Text == "Cancel")
                        Bot.SendTextMessageAsync(msg.Chat.Id, "Cancelled", replyMarkup: new ReplyKeyboardRemove()).Wait();
                    else
                    {
                        UpdatePrefs(new Dictionary<string, string>() { { dayKey, msg.Text } }, msg.Chat.Id);
                        Bot.SendTextMessageAsync(msg.Chat.Id, $"{day} set to {msg.Text}", replyMarkup: new ReplyKeyboardRemove()).Wait();
                    }
                    
                    PrefsCommand(4, args);
                    break;
                default:
                    break;
            }
            return true;
        }

        // Change settings (/settings)
        private static bool SettingsCommand(int stage, Dictionary<string, string> args)
        {
            var msg = JsonConvert.DeserializeObject<Telegram.Bot.Types.Message>(args["msg"]);
            Telegram.Bot.Types.CallbackQuery callback;
            string cmdStr = "settings";

            InlineKeyboardMarkup inlineKeyboard;

            // Retrieve user from database
            var row = userdb.ExecuteQuery($"select auto_on,notif_on from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';").Rows[0];
                
            switch(stage)
            {
                // Root menu of options
                case 0:
                    var autoBool = Convert.ToBoolean(row["auto_on"]);
                    var autoText = "Turn Auto ";
                    if (autoBool)
                        autoText += "Off";
                    else
                        autoText += "On";

                    var notifBool = Convert.ToBoolean(row["notif_on"]);
                    var notifText = "Turn Notifs ";
                    if (notifBool)
                        notifText += "Off";
                    else
                        notifText += "On";

                    inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData(autoText)},
                        new[] { InlineKeyboardButton.WithCallbackData(notifText)},
                        //new[] { InlineKeyboardButton.WithCallbackData("Change email") },
                        //new[] { InlineKeyboardButton.WithCallbackData("Change password") },
                        new[] { InlineKeyboardButton.WithCallbackData("Delete record", "2") },
                        new[] { InlineKeyboardButton.WithCallbackData("Done")}
                    });

                    var inlineMsg = Bot.SendTextMessageAsync(msg.Chat.Id, "Edit settings", replyMarkup: inlineKeyboard).Result;
                    createCommandTimer(inlineMsg);
                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";
                    break;
                
                // Perform change (if no further input is required) or route to relevant stage if more input is required
                case 1:
                    if (!args.ContainsKey("callback"))
                        break;

                    removeCommandTimer(msg.Chat.Id);
                    callback = JsonConvert.DeserializeObject<Telegram.Bot.Types.CallbackQuery>(args["callback"]);
                    Bot.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);

                    if (callback.Data == "Done")
                    {
                        currentCommands.Remove(msg.Chat.Id);
                        break;
                    }
                    else if (callback.Data.StartsWith("Turn Auto"))
                    {
                        // Switch auto from "On" to "Off" or vice versa
                        int autoOn;
                        if (callback.Data.Contains("On"))
                            autoOn = 1;
                        else
                            autoOn = 0;

                        userdb.ExecuteNonQuery($"update user_accounts set auto_on = {autoOn} where telegram_id = '{Convert.ToString(msg.Chat.Id)}';");
                        callback.Data = "0";
                    }
                    else if (callback.Data.StartsWith("Turn Notifs"))
                    {
                        // Switch notifs from "On" to "Off" or vice versa
                        int notifsOn;
                        if (callback.Data.Contains("On"))
                            notifsOn = 1;
                        else
                            notifsOn = 0;

                        userdb.ExecuteNonQuery($"update user_accounts set notif_on = {notifsOn} where telegram_id = '{Convert.ToString(msg.Chat.Id)}';");
                        callback.Data = "0";
                    }
                    
                    SettingsCommand(Convert.ToInt32(callback.Data), args);
                    break;
                
                // Make sure that the user wants to delete their record
                case 2:
                    inlineKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("Yes"), InlineKeyboardButton.WithCallbackData("No") }
                    });

                    inlineMsg = Bot.SendTextMessageAsync(msg.Chat.Id, "Are you sure you want to delete your record?", replyMarkup: inlineKeyboard).Result;
                    createCommandTimer(inlineMsg);

                    currentCommands[msg.Chat.Id] = $"{cmdStr}-{++stage}";
                    break;

                // Delete user's record or return to root menu
                case 3:
                    removeCommandTimer(msg.Chat.Id);
                    Bot.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
                    callback = JsonConvert.DeserializeObject<Telegram.Bot.Types.CallbackQuery>(args["callback"]);

                    if (callback.Data == "Yes")
                    {
                        userdb.ExecuteNonQuery($"delete from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';");
                        currentCommands.Remove(msg.Chat.Id);
                        if (currentSessions.ContainsKey(msg.Chat.Id))
                        {
                            currentSessions[msg.Chat.Id].Close();
                            currentSessions.Remove(msg.Chat.Id);
                        }
                    }
                    else
                        SettingsCommand(0, args);

                    break;

                default:
                    break;
            }

            return true;
        }

        // Get the next day's Flex (/getnext)
        private static bool TomorrowCommand(int stage, Dictionary<string, string> args)
        {
            var msg = JsonConvert.DeserializeObject<Telegram.Bot.Types.Message>(args["msg"]);

            Bot.SendTextMessageAsync(msg.Chat.Id, "Retrieving session info...");

            // Sign in if there isn't an existing EDFSession
            if (!currentSessions.ContainsKey(msg.Chat.Id))
            {
                bool isSignedIn = Signin(args);
                if (!isSignedIn)
                {
                    currentCommands.Remove(msg.Chat.Id);
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Login failed.");
                    return false;
                }
            }

            // Retrieve the next Flex session
            var sessDate = currentSessions[msg.Chat.Id].GetDate();
            sessDate = sessDate.Substring(0, sessDate.Length - 6);
            var sess = currentSessions[msg.Chat.Id].GetSessionTomorrow();

            var msgText = $"{sessDate}\n\n{sess}";

            Bot.SendTextMessageAsync(msg.Chat.Id, msgText);
            currentCommands.Remove(msg.Chat.Id);

            return true;
        }

        // Get today's Flex (/gettoday)
        private static bool GetTodayCommand(int stage, Dictionary<string, string> args)
        {
            var msg = JsonConvert.DeserializeObject<Telegram.Bot.Types.Message>(args["msg"]);

            Bot.SendTextMessageAsync(msg.Chat.Id, "Retrieving session info...");

            if (!currentSessions.ContainsKey(msg.Chat.Id))
            {
                bool isSignedIn = Signin(args);
                if (!isSignedIn)
                {
                    currentCommands.Remove(msg.Chat.Id);
                    Bot.SendTextMessageAsync(msg.Chat.Id, "Login failed.");
                    return false;
                }
            }

            var sess = currentSessions[msg.Chat.Id].GetSessionToday();

            var msgText = sess;

            Bot.SendTextMessageAsync(msg.Chat.Id, msgText);
            currentCommands.Remove(msg.Chat.Id);

            return true;
        }

        // Utility function for easily updating user preferences
        private static void UpdatePrefs(Dictionary<string, string> newPrefs, long chatId)
        {
            // Get the current (old) preferences and copy them to a new dictionary
            var oldPrefsJson = (string)userdb.ExecuteQuery($"select prefs from user_accounts where telegram_id='{Convert.ToString(chatId)}';").Rows[0]["prefs"];
            var updatedPrefsDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(oldPrefsJson);

            // Iterate through the new preferences and update the relevant values in the updatedPrefsDict
            foreach (var kvp in newPrefs)
                updatedPrefsDict[kvp.Key] = kvp.Value;

            // Serialize the updated dictionary and save to database
            var updatedPrefsJson = JsonConvert.SerializeObject(updatedPrefsDict);
            userdb.ExecuteNonQuery($"update user_accounts set prefs = @PREFS where telegram_id = '{Convert.ToString(chatId)}';",
                new Dictionary<SQLiteParameter, object> { { new SQLiteParameter("@PREFS", DbType.String), updatedPrefsJson } });
        }

        // Create and start the timeout timer for commands
        private static void createCommandTimer(Telegram.Bot.Types.Message msg)
        {
            System.Timers.Timer timer = new System.Timers.Timer(cmdTimeout*1000);
            timer.Elapsed += (s, e) =>
            {
                commandTimers.Remove(msg.Chat.Id);
                currentCommands.Remove(msg.Chat.Id);
                Bot.DeleteMessageAsync(msg.Chat.Id, msg.MessageId);
            };
            timer.AutoReset = false;
            timer.Start();

            commandTimers.Add(msg.Chat.Id, timer);
        }

        // Remove a timer from the list of command timers
        private static void removeCommandTimer(long id)
        {
            commandTimers[id].Stop();
            commandTimers.Remove(id);
        }

        // Handle received messages
        private static async void BotOnMessageReceived(object sender, MessageEventArgs messageEventArgs)
        {
            var msg = messageEventArgs.Message;
            // Only want messages with text content
            if (msg == null || msg.Type != MessageType.Text) return;
            
            try
            {
                // Only want messages that start with the command symbol
                if (msg.Text.StartsWith("/"))
                {
                    // Want to check if the user has a record before letting them use commands other than /start
                    if (msg.Text.Split(' ').First() != "/start")
                    {
                        var knownUserDt = userdb.ExecuteQuery($"select * from user_accounts where telegram_id='{Convert.ToString(msg.Chat.Id)}';");
                        if (knownUserDt.Rows.Count == 0)
                        {
                            await Bot.SendTextMessageAsync(msg.Chat.Id, "A record doesn't exist for this user.  Use /start to create a record.");
                            return;
                        }
                        else if(knownUserDt.Rows[0]["password"].GetType() == typeof(DBNull))
                        {
                            await Bot.SendTextMessageAsync(msg.Chat.Id, "Finish entering required information in /start");
                            return;
                        }
                    }

                    // Check if the user already has a command in progress and interrupt it if so
                    if (currentCommands.ContainsKey(msg.Chat.Id))
                    {
                        currentCommands.Remove(msg.Chat.Id);
                        await Bot.SendTextMessageAsync(msg.Chat.Id, "Interrupting previous command", replyMarkup: new ReplyKeyboardRemove());

                        if (commandTimers.ContainsKey(msg.Chat.Id))
                            commandTimers[msg.Chat.Id].Interval = 1;
                    }
                    else
                    {
                        // Prevent uesrs from starting new commands if the bot is about to be shut down while letting users who are in the middle of commands finish them
                        if (msg.Chat.Id != adminId && updateBool)
                        {
                            await Bot.SendTextMessageAsync(msg.Chat.Id, "Flex Bot is going offline for updates.  Please check back later.");
                            return;
                        }
                    }

                }
            }
            catch (ApiRequestException e)
            {
                string err = $"Error\nInner Exception: {e.InnerException.ToString()}\nMsg: {e.Message}\nStack Trace: {e.StackTrace}\n\n";

                try
                {
                    await Bot.SendTextMessageAsync(adminId, err);
                }
                catch (ApiRequestException)
                {
                    Console.WriteLine(err);
                }
            }

            if (currentSessions.ContainsKey(msg.Chat.Id))
                currentSessions[msg.Chat.Id].StopTimeout();

            var msgJson = JsonConvert.SerializeObject(msg);
            var args = new Dictionary<string, string>() { { "msg", msgJson } };

            try
            {
                // Call associated command function
                switch (msg.Text.Split(' ').First())
                {
                    case "/start":
                        currentCommands.Add(msg.Chat.Id, "start-0");
                        StartCommand(0, args);
                        break;
                    // Send list of all commands with short descriptions of each
                    case "/help":
                        Bot.SendTextMessageAsync(msg.Chat.Id, "Below is a list of all available commands.  If you have any questions feel free to message @JoshLaCount").Wait();
                        using (FileStream file = File.Open(curDir + @"\telegram commands.txt", FileMode.Open))
                        {
                            byte[] buffer = new byte[(int)file.Length];
                            file.Read(buffer, 0, (int)file.Length);
                            Bot.SendTextMessageAsync(msg.Chat.Id, Encoding.UTF8.GetString(buffer)).Wait();
                        }
                        break;
                    // An admin only command for testing features and fixes
                    case "/test":
                        if (msg.Chat.Id == adminId)
                        {
                        }
                        break;
                    case "/setnext":
                        currentCommands.Add(msg.Chat.Id, "singlesess-0");
                        SingleSessionSelectCommand(0, args);
                        break;
                    case "/prefs":
                        currentCommands.Add(msg.Chat.Id, "prefs-0");
                        PrefsCommand(0, args);
                        break;
                    case "/getnext":
                        currentCommands.Add(msg.Chat.Id, "tomorrow-0");
                        TomorrowCommand(0, args);
                        break;
                    case "/settings":
                        currentCommands.Add(msg.Chat.Id, "settings-0");
                        SettingsCommand(0, args);
                        break;
                    // For when the bot is about to be taken offline
                    case "/update":
                        if (msg.Chat.Id == adminId)
                        {
                            // Send current number of commands and update count
                            updateBool = true;
                            var countMsg = Bot.SendTextMessageAsync(adminId, $"{currentCommands.Count} commands").Result;
                            var timer = new System.Timers.Timer(1000);
                            timer.Elapsed += (s, e) =>
                            {
                                Bot.EditMessageTextAsync(adminId, countMsg.MessageId, $"{currentCommands.Count} commands");

                                if (currentCommands.Count == 0)
                                {
                                    Bot.SendTextMessageAsync(adminId, "Ready for update");
                                    timer.Stop();
                                }
                            };
                            timer.Start();
                        }
                        break;
                    case "/gettoday":
                        currentCommands.Add(msg.Chat.Id, "today-0");
                        GetTodayCommand(0, args);
                        break;
                    // Send out a message to all users
                    case "/notify":
                        if (msg.Chat.Id == adminId)
                        {
                            var splitMsg = msg.Text.Split(' ');
                            string notif = "Admin Notification:\n" + string.Join(" ", splitMsg, 1, splitMsg.Length - 1);

                            var users = userdb.ExecuteQuery("select * from user_accounts where password is not null");

                            foreach (DataRow row in users.Rows)
                            {
                                Bot.SendTextMessageAsync((string)row["telegram_id"], notif);
                            }
                        }
                        break;
                    // If the user isn't starting a command, they must be continuing one, so progress said command to the next stage
                    default:
                        if (currentCommands.ContainsKey(msg.Chat.Id))
                        {
                            var cmdInfo = currentCommands[msg.Chat.Id].Split('-');
                            commandDict[cmdInfo[0]](Convert.ToInt32(cmdInfo[1]), args);
                        }
                        break;
                }
            }
            catch (ApiRequestException e)
            {
                string err = $"Error\nInner Exception: {e.InnerException.ToString()}\nMsg: {e.Message}\nStack Trace: {e.StackTrace}\n\n";

                try
                {
                    await Bot.SendTextMessageAsync(adminId, err);
                }
                catch (ApiRequestException)
                {
                    Console.WriteLine(err);
                }
            }
            if (currentSessions.ContainsKey(msg.Chat.Id))
                currentSessions[msg.Chat.Id].StartTimeout();
        }
            
        // Handle callbacks (InlineKeyboards)
        private static async void BotOnCallbackQueryReceived(object sender, CallbackQueryEventArgs callbackQueryEventArgs)
        {
            var callbackQuery = callbackQueryEventArgs.CallbackQuery;
            var msg = callbackQuery.Message;

            var msgJson = JsonConvert.SerializeObject(msg);
            var cbJson = JsonConvert.SerializeObject(callbackQuery);
            var args = new Dictionary<string, string>() { { "msg", msgJson }, { "callback", cbJson } };

            // Progress command to next stage
            if (currentCommands.ContainsKey(msg.Chat.Id))
            {
                var cmdInfo = currentCommands[msg.Chat.Id].Split('-');
                commandDict[cmdInfo[0]](Convert.ToInt32(cmdInfo[1]), args);
            }
            
            await Bot.AnswerCallbackQueryAsync(
                callbackQuery.Id,
                "Received");
                
        }
    }
}
