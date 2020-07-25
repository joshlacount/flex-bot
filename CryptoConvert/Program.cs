using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EncryptString;
using SQLiteDatabase;
using System.Security.Cryptography;
using System.Data;

// I switched encryption methods, so I needed to re-encrypt all the stored user data using the new method.
namespace CryptoConvert
{
    class Program
    {
        private static string dataFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + @"\NestBots";
        private static SQLite userdb = new SQLite(dataFolderPath + @"\users.db");

        static void Main(string[] args)
        {
            var users = userdb.ExecuteQuery("select * from user_accounts where password is not null");

            foreach(DataRow row in users.Rows)
            {
                var passCipher = (string)row["password"];
                var salt = (string)row["password_salt"];

                var passClear = Encoding.Unicode.GetString(ProtectedData.Unprotect(Convert.FromBase64String(passCipher), Convert.FromBase64String(salt), DataProtectionScope.CurrentUser));
                string encryptedString = StringCipher.Encrypt(passClear, salt);

                userdb.ExecuteNonQuery($"update user_accounts set password = \"{encryptedString}\" where telegram_id = '{(string)row["telegram_id"]}';");
            }
        }
    }
}
