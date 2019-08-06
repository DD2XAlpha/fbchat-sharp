﻿using fbchat_sharp.API;
using Newtonsoft.Json.Linq;
using Polenter.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace wpfapp
{
    // Like FBClient_Simple but also saves session cookies
    public class FBClient_Wpf : MessengerClient
    {
        private static readonly string appName = "FBChat-Sharp";
        private static readonly string sessionFile = "SESSION_COOKIES_wpf.dat";

        #region EVENTS
        /// <summary>
        /// Subscribe to this event to get chat updates (e.g. a new message)
        /// </summary>
        public event EventHandler<UpdateEventArgs> UpdateEvent;

        /// <summary>
        /// Enum for messenger update events
        /// </summary>
        public enum UpdateStatus
        {
            /// <summary>
            /// A new message was received
            /// </summary>
            NEW_MESSAGE
        }

        /// <summary>
        /// 
        /// </summary>
        public class UpdateEventArgs : EventArgs
        {
            /// <returns>
            /// EventType: UpdateStatus enum associated with this event        
            /// </returns>
            public UpdateStatus EventType { get; }
            /// <returns>
            /// Payload: object associated with this event, e.g. a FB_Message 
            /// </returns>
            public object Payload { get; }

            /// <param name="_update_event">UpdateStatus enum associated with this event</param>
            /// <param name="_data">object associated with this event, e.g. a FB_Message</param>
            public UpdateEventArgs(UpdateStatus _update_event, object _data)
            {
                this.EventType = _update_event;
                this.Payload = _data;
            }
        }

        protected override async Task onMessage(string mid = null, string author_id = null, string message = null, FB_Message message_object = null, string thread_id = null, ThreadType thread_type = ThreadType.USER, long ts = 0, JToken metadata = null, JToken msg = null)
        {
            UpdateEvent?.Invoke(this, new UpdateEventArgs(UpdateStatus.NEW_MESSAGE, message_object));
            await Task.Yield();
        }
        #endregion

        #region COOKIES
        protected override async Task DeleteCookiesAsync()
        {
            try
            {
                await Task.Yield();
                var file = Path.Combine(UserDataFolder, sessionFile);
                File.Delete(file);
            }
            catch (Exception ex)
            {
                this.Log(ex.ToString());
            }
        }      

        protected override async Task<Dictionary<string, List<Cookie>>> ReadCookiesFromDiskAsync()
        {
            try
            {
                var file = Path.Combine(UserDataFolder, sessionFile);
                using (var fileStream = File.OpenRead(file))
                {
                    await Task.Yield();
                    var settings = new SharpSerializerBinarySettings(BinarySerializationMode.Burst);
                    var serializer = new SharpSerializer(settings);
                    return (Dictionary<string, List<Cookie>>)serializer.Deserialize(fileStream);
                }
            }
            catch (Exception ex)
            {
                this.Log(string.Format("Problem reading cookies from disk: {0}", ex.ToString()));
                return null;
            }
        }

        protected override async Task WriteCookiesToDiskAsync(Dictionary<string, List<Cookie>> cookieJar)
        {
            var file = Path.Combine(UserDataFolder, sessionFile);

            using (var fileStream = File.Create(file))
            {
                try
                {
                    var settings = new SharpSerializerBinarySettings(BinarySerializationMode.Burst);
                    var serializer = new SharpSerializer(settings);
                    serializer.Serialize(cookieJar, fileStream);
                    await fileStream.FlushAsync();
                }
                catch (Exception ex)
                {
                    this.Log(string.Format("Problem writing cookies to disk: {0}", ex.ToString()));
                }
            }
        }
        #endregion

        /// <summary>
        /// Get the current user data folder
        /// </summary>
        private static string UserDataFolder
        {
            get
            {
                string folderBase = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string dir = Path.Combine(folderBase, appName.ToUpper());
                return CheckDir(dir);
            }
        }

        /// <summary>
        /// Check the specified folder, and create if it doesn't exist.
        /// </summary>
        /// <param name="dir"></param>
        /// <returns></returns>
        private static string CheckDir(string dir)
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return dir;
        }
    }
}