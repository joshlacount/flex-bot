using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Data;
using System.Data.SQLite;
using System.Threading;
using Newtonsoft.Json;
using System.IO;

using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

using EDF_API;
using SQLiteDatabase;
using EncryptString;

namespace Auto_Request
{
    class Program
    {
        private static readonly long adminId;    // My telegram ID - value removed for repo

        private static Dictionary<string, List<string>> userNotifs = new Dictionary<string, List<string>>();    // List of morning notifications by user
        private static string notifsJson;    // JSON string of notification list

        private static string dataFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\NestBots";    // Path of folder where data is stored
        private static string notifFilePath = dataFolderPath + @"\notifs.json";    // Path of notifications file
        private static string backupFilePath = dataFolderPath + @"\backup.json";    // Path of notifications backup file
        private static string noFlexFilePath = dataFolderPath + @"\no-flex.txt";    // Path of file that contains the next date of no Flex when there usually is
        private static SQLite userdb = new SQLite(dataFolderPath + @"\users.db");    // User database

        private static DayOfWeek[] flexDays = new DayOfWeek[] { DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };    // Days that have Flex sessions

        private static bool shouldGetToday;    // Retrieve today's Flex?
        private static bool shouldSetSess;    // Set today's Flex?
        private static bool isCorrectDate = false;    // The date of the next available Flex session is actually tomorrow

        static void Main(string[] args)
        {
            // Identifier for Notifier to make sure Auto Request ran
            userNotifs.Add("ran", new List<string>());

            // Get the current date and a list of users who have a password set
            var dt = DateTime.Now;
            var users = userdb.ExecuteQuery("select * from user_accounts where password is not null");

            // If neither tomorrow nor today is a Flex day, save notifs and exit.
            if (!(flexDays.Contains(dt.AddDays(1).DayOfWeek) || flexDays.Contains(dt.DayOfWeek)))
            {
                notifsJson = JsonConvert.SerializeObject(userNotifs);
                using (FileStream file = File.Open(notifFilePath, FileMode.Create))
                {
                    byte[] bytesToWrite = Encoding.UTF8.GetBytes(notifsJson);
                    file.Write(bytesToWrite, 0, bytesToWrite.Length);
                }

                return;
            }

            // Check the date in the No Flex file to determine if today's Flex should be retrieved
            using (FileStream file = File.Open(noFlexFilePath, FileMode.Open))
            {
                byte[] buffer = new byte[(int)file.Length];
                file.Read(buffer, 0, (int)file.Length);
                var dateStr = Encoding.UTF8.GetString(buffer);
                var todayStr = dt.ToLongDateString();
                shouldGetToday = flexDays.Contains(dt.DayOfWeek) && (dateStr != todayStr);
            }

            // Want to set next Flex if tomorrow is a Flex day
            shouldSetSess = flexDays.Contains(dt.AddDays(1).DayOfWeek);
            
            // Iterate through users and get and/or set Flex
            foreach (DataRow row in users.Rows)
            {
                // List to store notifs for user
                List<string> notifs = new List<string>();

                // Store email and password
                var email = (string)row["email"];
                var passCipher = (string)row["password"];
                var salt = (string)row["password_salt"];

                var passClear = StringCipher.Decrypt(passCipher, salt);
                var edf = new EDFSession(email, passClear);
                bool isSignedIn = edf.SignIn();

                if (isSignedIn)
                {
                    // Retrieve today's Flex if we should and the user has notifs turned on
                    if (shouldGetToday && (Int64)row["notif_on"] == 1)
                    {
                        string tdySess = edf.GetSessionToday();
                        if (tdySess == "")
                            notifs.Add("Failed to retrieve today's flex.");
                        else
                            notifs.Add("Today's " + tdySess);
                    }
                    
                    // Set tomorrow's Flex if we should and the user has auto sign up turned on
                    if (shouldSetSess && (Int64)row["auto_on"] == 1)
                    {
                        // Determine if tomorrow is actually the next available Flex session and that we should set the next Flex.
                        if (!isCorrectDate)
                        {
                            string nextAvailDate = edf.GetDate();
                            string tomorrowDate = dt.AddDays(1).ToLongDateString();
                            
                            string[] tomorrowDateSplit = tomorrowDate.Split(' ');
                            tomorrowDateSplit[1] = tomorrowDateSplit[1].Substring(0, 3);
                            if (tomorrowDateSplit[2].Length == 2)
                                tomorrowDateSplit[2] = "0" + tomorrowDateSplit[2];

                            shouldSetSess = shouldSetSess && (nextAvailDate == string.Join(" ", tomorrowDateSplit) && edf.GetAvailableSessions().Count > 0);
                            
                            if (!shouldSetSess)
                            {
                                using (FileStream file = File.Open(noFlexFilePath, FileMode.Create))
                                {
                                    byte[] bytesToWrite = Encoding.UTF8.GetBytes(tomorrowDate);
                                    file.Write(bytesToWrite, 0, bytesToWrite.Length);
                                }
                                edf.Close();
                                userNotifs.Add((string)row["telegram_id"], notifs);
                                continue;
                            }
                            isCorrectDate = true;
                        }
                        
                        // Get the user's prefs
                        Dictionary<string, string> prefs = JsonConvert.DeserializeObject<Dictionary<string, string>>((string)row["prefs"]);
                        var teacher = prefs[dt.AddDays(1).DayOfWeek.ToString().Substring(0, 3).ToLower()];
                        
                        // Sign up for either the user's default teacher or whatever teacher they chose for that day
                        if (teacher == "default" && prefs["default"] != "")
                        {
                            var success = edf.SelectSession(prefs["default"]);
                            
                            if (!success)
                            {
                                notifs.Add("Tomorrow's session has not been set.  Default session unavailable.");
                            }
                        }
                        else
                        {
                            var success = edf.SelectSession(teacher);
                            
                            if (!success)
                            {
                                success = edf.SelectSession(prefs["default"]);
                                if (success)
                                    notifs.Add("Schedule session not available for tomorrow.  Default session has been signed up for instead");
                                else
                                    notifs.Add("Tomorrow's session has not been set.  Both default and schedule session are not available for tomorrow.");
                            }
                        }
                        edf.AcceptAlert(false);
                        Thread.Sleep(1000);
                    }
                    edf.Close();
                }
                else
                    notifs.Add("Login failed.");

                // Add user's notifs to master list of notifs
                userNotifs.Add((string)row["telegram_id"], notifs);
            }

            // Serialize notifs list and write to file
            notifsJson = JsonConvert.SerializeObject(userNotifs);
            
            using (FileStream file = File.Open(notifFilePath, FileMode.Create))
            {
                byte[] bytesToWrite = Encoding.UTF8.GetBytes(notifsJson);
                file.Write(bytesToWrite, 0, bytesToWrite.Length);
            }

            using (FileStream file = File.Open(backupFilePath, FileMode.Create))
            {
                byte[] bytesToWrite = Encoding.UTF8.GetBytes(notifsJson);
                file.Write(bytesToWrite, 0, bytesToWrite.Length);
            }
        }
    }
}
