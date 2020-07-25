using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Telegram.Bot;
using Newtonsoft.Json;
using SQLiteDatabase;
using System.Data;
using System.Threading;

namespace Notifier
{
    class Program
    {
        private static readonly string adminId;    // My telegram ID - value removed for repo
        private static readonly string token;    // Bot token - value removed for repo
        private static readonly string testToken;    // Bot token for testing - value removed for repo
        private static readonly TelegramBotClient Bot = new TelegramBotClient(testToken);

        private static readonly string dataFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\NestBots";    // Path of folder where data is stored
        private static readonly string notifFilePath = dataFolderPath + @"\notifs.json";    // Path of notifications file

        private static Dictionary<string, List<string>> notifs = new Dictionary<string, List<string>>();    // List of morning notifications by user

        static void Main(string[] args)
        {
            // Get stored notifs
            using (FileStream file = File.Open(notifFilePath, FileMode.Open))
            {
                byte[] buffer = new byte[(int)file.Length];
                file.Read(buffer, 0, (int)file.Length);
                var jsonString = Encoding.UTF8.GetString(buffer);
                notifs = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(jsonString);
            }

            // If auto request ran that morning, send out notifications
            if (notifs.Keys.Contains("ran"))
            {
                notifs.Remove("ran");

                foreach (var kvp in notifs)
                {
                    if (kvp.Value.Count > 0)
                    {
                        foreach (var msg in kvp.Value)
                        {
                            try
                            {
                                Bot.SendTextMessageAsync(kvp.Key, msg).Wait();
                            }
                            catch(Exception e)
                            {
                                Bot.SendTextMessageAsync(adminId, e.Message).Wait();
                            }
                        }
                    }
                }
            }
            else
            {
                Bot.SendTextMessageAsync(adminId, "Auto request failed to run").Wait();
            }
            
            // Wipe notifs file
            notifs = new Dictionary<string, List<string>>();
            string notifsJson = JsonConvert.SerializeObject(notifs);
            
            using (FileStream file = File.Open(notifFilePath, FileMode.Create))
            {
                byte[] bytesToWrite = Encoding.UTF8.GetBytes(notifsJson);
                file.Write(bytesToWrite, 0, bytesToWrite.Length);
            }
            
        }
    }
}
