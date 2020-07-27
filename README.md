# Flex Bot

### Intro

At the high school I went to, Flex was a daily 20 min study time.  Students signed up for which one of their teachers they wanted to go to for each day, and forgetting to sign up for a day resulted in being signed up for a random teacher.  I personally found it tedious to have to remember to sign up each day since I usually picked the same teacher each time, and forgetting to sign up could result in me getting stuck with an unfavorable teacher.  Enter Flex Bot.



### Overview

I built Flex Bot to automatically sign me (and others) up for Flex sessions according to a user-defined schedule.  The bot is actually split across three programs.  The main program (Flex Bot) takes commands from users through Telegram.  First, users must register using their school email and password; user information is stored in a local SQLite database and retrieved through their Telegram ID.  Once registered, they can set a schedule of teachers they'd like each week whether that's a specific teacher for a specific day of the week or the same teacher for the whole week.  The actual signing up of sessions is handled by the Auto Request program.  This program runs early in the morning each day iterates through the users in the database, signing them up for the next day.  The final part is the Notifier which, before school starts, sends users a message of which Flex session they have that day.  Both the Auto Request and Notifier are scheduled through Windows' Task Scheduler.



### EDF API

Sign up for Flex sessions was done on a website powered by [Edficiency](https://www.edficiency.com/) (EDF).  Since there wasn't an API for me to use, I had to write my own way to sign up for sessions through code.  Searching for ways to accomplish this, I discovered [Selenium](https://www.selenium.dev/), a browser automation tool.  I used Selenium's C# API to write functions to sign users into EDF, get a list of available teachers for a session, sign users up for specific teacher, etc.