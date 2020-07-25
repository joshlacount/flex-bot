using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using SeleniumExtras.WaitHelpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace EDF_API
{
    public class EDFSession
    {
        private IWebDriver driver;
        private WebDriverWait driverWait;
        private readonly string mainWindowHandle;

        private readonly Dictionary<string, string> userCreds;

        private readonly string edfUrl = "https://dphs.edf.school";
        private Dictionary<string, string> availableSessions;

        private const int timeout = 180;
        private System.Timers.Timer timeoutTimer;

        public EDFSession(string email, string passw)
        {
            // Start ChromeDriver in headless mode
            ChromeOptions driverOptions = new ChromeOptions();
            driverOptions.AddArguments(new string[] { "--headless", "unsafe-inline", "log-level=3" });
            driver = new ChromeDriver(driverOptions);
            driverWait = new WebDriverWait(driver, TimeSpan.FromSeconds(10));

            mainWindowHandle = driver.CurrentWindowHandle;

            userCreds = new Dictionary<string, string>()
            {
                { "email", email },
                { "passw", passw }
            };

            timeoutTimer = new System.Timers.Timer(timeout * 1000.0);
            timeoutTimer.AutoReset = false;
        }

        // Set timeout event
        public void SetTimeout(ElapsedEventHandler elapsedEvent)
        {
            timeoutTimer.Elapsed += elapsedEvent;
        }

        // Start the timeout
        public void StartTimeout()
        {
            timeoutTimer.Start();
        }

        // Stop the timeout
        public void StopTimeout()
        {
            timeoutTimer.Stop();
        }

        // Sign the user in to EDF
        public bool SignIn()
        {
            // Go to website
            driver.Navigate().GoToUrl(edfUrl+"/index.php");

            // Find Google sign in button and click it
            try
            {
                driver.FindElement(By.XPath("//img[@onclick='signIn()']")).Click();
            }
            catch (NoSuchElementException e)
            {
                Console.WriteLine("Sign In button not found");
                Close();
                return false;
            }

            // The sign in window used to be a popup, but that's not the case anymore
            /*
            string signInWindowHandle = null;
            while (signInWindowHandle == null)
            {
                foreach (var handle in driver.WindowHandles)
                {
                    if (handle != mainWindowHandle)
                    {
                        signInWindowHandle = handle;
                        break;
                    }
                }
            }
            driver.SwitchTo().Window(signInWindowHandle);
            */

            // Find the email text field and enter email
            IWebElement elem;
            try
            {
                elem = driverWait.Until<IWebElement>(ExpectedConditions.ElementIsVisible(By.XPath("//input[@type='email']")));
                elem.SendKeys(userCreds["email"]);
                elem.SendKeys(Keys.Return);
            }
            catch (WebDriverTimeoutException)
            {
                Console.WriteLine("Couldn't find email element");
                Close();
                return false;
            }

            // Find the password text field and enter password
            try
            {
                elem = driverWait.Until<IWebElement>(ExpectedConditions.ElementToBeClickable(By.XPath("//input[@type='password']")));
                elem.SendKeys(userCreds["passw"]);
                elem.SendKeys(Keys.Return);
            }
            catch (WebDriverTimeoutException)
            {
                Console.WriteLine("Couldn't find password element");
                Close();
                return false;
            }

            //driver.SwitchTo().Window(mainWindowHandle);

            // Wait for login to go through and complete
            try
            {
                driverWait.Until(ExpectedConditions.ElementIsVisible(By.XPath("//div[contains(text(), 'logged in as')]")));
                return true;
            }
            catch
            {
                Console.WriteLine("Login failed");
            }
            Close();
            return false;
        }

        // Navigate to one of the pages on the EDF site
        public bool NavToPage(string pageName)
        {
            switch (pageName)
            {
                case "home":
                    driver.Navigate().GoToUrl(edfUrl+"/index.php");
                    break;
                case "request":
                    driver.Navigate().GoToUrl(edfUrl+"/requestsession.php");
                    break;
                case "profile":
                    driver.Navigate().GoToUrl(edfUrl+"/updateprofile.php");
                    break;
                default:
                    Console.WriteLine("That page doesn't exist");
                    return false;
            }
            Thread.Sleep(1000);
            return true;
        }

        // Select next session by teacher name
        public bool SelectSession(string teacherName)
        {
            if (driver.Url != edfUrl + "/requestsession.php")
                NavToPage("request");

            UpdateAvailableSessions();
            
            try
            {
                driver.FindElement(By.XPath($"//option[@value='{availableSessions[teacherName]}']")).Click();
                driver.FindElement(By.XPath("//button[@id='btnAddChoice']")).Click();
                return true;
            }
            catch (Exception e)
            {
                if (e.GetType() == typeof(NoSuchElementException))
                    Console.WriteLine("Session element not found");
                else if (e.GetType() == typeof(KeyNotFoundException))
                {
                    Console.WriteLine("Teacher name not in session list");
                    //UpdateAvailableSessions();
                    //driver.FindElement(By.XPath($"//option[@value='{availableSessions[teacherName]}']")).Click();
                    //driver.FindElement(By.XPath("//button[@id='btnAddChoice']")).Click();
                }
                else
                    Console.WriteLine("Error with session selection: " + e.Message);
            }

            return false;
        }

        // Close browser
        public void Close()
        {
            driver.Quit();
            driver.Dispose();
        }

        // Update list of next sessions
        private void UpdateAvailableSessions()
        {
            if (driver.Url != edfUrl + "/requestsession.php")
                NavToPage("request");

            var d = new Dictionary<string, string>();

            var options = driver.FindElements(By.XPath("//select[@id='sessions']/*"));
            foreach (var elem in options)
            {
                d.Add(elem.Text.Split('|')[0].Trim(' ').Replace(",", string.Empty).ToLower(), elem.GetAttribute("value"));
            }
            d.Remove(string.Empty);

            availableSessions = d;
        }

        // Get list of next sessions
        public Dictionary<string, string> GetAvailableSessions()
        {
            UpdateAvailableSessions();
            return availableSessions;
        }

        // Has the user already requested a next session
        public bool HasRequested()
        {
            if (driver.Url != edfUrl + "/requestsession.php")
                NavToPage("request");
            try
            {
                driver.FindElement(By.XPath("//span[@class='hasRequest']"));
                return true;
            }
            catch (NoSuchElementException)
            {
                return false;
            }
        }

        // Get today's session
        public string GetSessionToday()
        {
            var str = "";
            if (driver.Url != edfUrl + "/index.php")
                NavToPage("home");

            try
            {
                var elem = driver.FindElement(By.TagName("td"));
                str = elem.Text;
            }
            catch (NoSuchElementException e)
            {
            }

            return str;
        }

        // Get tomorrow's requested session
        public string GetSessionTomorrow()
        {
            if (driver.Url != edfUrl + "/requestsession.php")
                NavToPage("request");

            var elem = driver.FindElement(By.XPath("//span[@id='sessioninfo']"));

            string sess;
            if (elem.Text.Contains("not made a request"))
                sess = "Next session has not been set";
            else
                sess = string.Join("\n", elem.Text.Split('\n'), 0, 3);

            return sess;
        }

        // Get the date of the next session
        public string GetDate()
        {
            string dateStr;

            if (driver.Url != edfUrl + "/requestsession.php")
                NavToPage("request");

            try
            {
                dateStr = driver.FindElement(By.XPath("//input[@id='date']")).GetAttribute("value");
            }
            catch (NoSuchElementException e)
            {
                Console.WriteLine("Date element not found");
                dateStr = "";
            }

            return dateStr;
        }

        // Accept an alert from the website.  The site gives an alert when requesting a session on a day where a session has already been requested.
        public void AcceptAlert(bool shouldAccept)
        {
            if (shouldAccept)
            {
                try
                {
                    driver.SwitchTo().Alert().Accept();
                }
                catch (NoAlertPresentException)
                { }
            }
            else
            {
                try
                {
                    driver.SwitchTo().Alert().Dismiss();
                }
                catch (NoAlertPresentException)
                { }
            }
            Thread.Sleep(500);
        }
    }
}
