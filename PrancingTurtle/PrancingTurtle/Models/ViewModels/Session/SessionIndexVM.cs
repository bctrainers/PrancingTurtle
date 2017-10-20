﻿using System;
using System.Collections.Generic;

namespace PrancingTurtle.Models.ViewModels.Session
{
    public class SessionIndexVM
    {
        public List<Database.Models.Session> Sessions { get; set; }
        public string TimeZoneId { get; set; }

        public TimeSpan TimeZoneOffset
        {
            get { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId).GetUtcOffset(DateTime.UtcNow); }
        }

        public SessionIndexVM()
        {
            // Default the timezone to UTC
            TimeZoneId = "UTC";
        }
    }
}