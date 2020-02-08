﻿using Dasync.Collections;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Client.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace fbchat_sharp.API
{
    /// <summary>
    /// 
    /// </summary>
    public class Client_Constants
    {
        public static readonly Dictionary<string, object> ACONTEXT = new Dictionary<string, object>()
        {
            { "action_history", new List<Dictionary<string,object>>() {
                new Dictionary<string,object>() { { "surface", "messenger_chat_tab" }, {"mechanism", "messenger_composer"} } }
            }
        };
    }

    /// <summary>
    /// A client for the Facebook Chat (Messenger).
    /// This contains all the methods you use to interact with Facebook.You can extend this
    /// class, and overwrite the ``on`` methods, to provide custom event handling (mainly
    /// useful while listening).
    /// </summary>
    public class Client
    {
        /// Whether the client is listening. Used when creating an external event loop to determine when to stop listening.
        private bool listening { get; set; }
        /// Stores and manages state required for most Facebook requests.
        private Session _session { get; set; }
        /// Mqtt client for receiving messages
        private IMqttClient mqttClient;

        private string _sticky = null;
        private string _pool = null;
        private int _sequence_id = 0;
        private int _mqtt_sequence_id = 0;
        private string _sync_token = null;
        private int _pull_channel = 0;
        private bool _markAlive = false;
        private Dictionary<string, FB_ActiveStatus> _buddylist = null;

        /// <summary>
        /// The ID of the client.
        /// Can be used as `thread_id`.
        /// Note: Modifying this results in undefined behaviour
        /// </summary>
        protected string _uid { get; set; }

        public Client()
        {
            this._sticky = null;
            this._pool = null;
            this._sequence_id = 0;
            this._mqtt_sequence_id = 0;
            this._sync_token = null;
            this._pull_channel = 0;
            this._markAlive = true;
            this._buddylist = new Dictionary<string, FB_ActiveStatus>();
        }

        /// <summary>
        /// Tries to login using a list of provided cookies.
        /// </summary>
        /// <param name="session_cookies">Cookies from a previous session</param>
        /// <param name="user_agent"></param>
        public async Task<Session> fromSession(Dictionary<string, List<Cookie>> session_cookies = null, string user_agent = null)
        {
            // If session cookies aren't set, not properly loaded or gives us an invalid session, then do the login
            if (
                session_cookies == null ||
                !await this.setSession(session_cookies, user_agent: user_agent) ||
                !await this.isLoggedIn()
                )
            {
                throw new FBchatException(message: "Login from session failed.");
            }

            return _session;
        }

        #region LOGIN METHODS

        /// <summary>
        /// Sends a request to Facebook to check the login status
        /// </summary>
        /// <returns>true if the client is still logged in</returns>
        public async Task<bool> isLoggedIn()
        {
            return await this._session.is_logged_in();
        }

        /// <summary>
        /// Retrieves session
        /// </summary>
        public Session getSession()
        {
            return this._session;
        }

        /// <summary>
        /// Loads session cookies
        /// </summary>
        /// <param name="session_cookies"></param>
        /// <param name="user_agent"></param>
        /// <returns>false if ``session_cookies`` does not contain proper cookies</returns>
        public async Task<bool> setSession(Dictionary<string, List<Cookie>> session_cookies, string user_agent = null)
        {
            try
            {
                // Load cookies into current session
                this._session = await Session.from_cookies(session_cookies, user_agent: user_agent);
                this._uid = this._session.get_user_id();
            }
            catch (Exception)
            {
                Debug.WriteLine("Failed loading session");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Uses ``email`` and ``password`` to login the user (If the user is already logged in, this will do a re-login)
        /// </summary>
        /// <param name="email">Facebook ``email`` or ``id`` or ``phone number``</param>
        /// <param name="password">Facebook account password</param>
        /// <param name="user_agent"></param>
        public async Task<Session> login(string email, string password, string user_agent = null)
        {
            await this.onLoggingIn(email: email);

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
                throw new FBchatUserError("Email and password not set");

            try
            {
                this._session = await Session.login(
                    email,
                    password,
                    on_2fa_callback: this.on2FACode,
                    user_agent: user_agent);
                this._uid = this._session.get_user_id();
                await this.onLoggedIn(email: email);
                return this._session;
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }

        /// <summary>
        /// Safely logs out the client
        /// </summary>
        /// <returns>true if the action was successful</returns>
        public async Task<bool> logout()
        {
            if (await this._session.logout())
            {
                this._session = null;
                this._uid = null;
                return true;
            }
            return false;
        }
        #endregion

        #region FETCH METHODS

        /// <summary>
        /// Get all threads in thread_location.
        /// Threads will be sorted from newest to oldest.
        /// </summary>
        /// <param name="thread_location">ThreadLocation: INBOX, PENDING, ARCHIVED or OTHER</param>
        /// <param name="before">Fetch only thread before this epoch (in ms) (default all threads)</param>
        /// <param name="after">Fetch only thread after this epoch (in ms) (default all threads)</param>
        /// <param name="limit">The max. amount of threads to fetch (default all threads)</param>
        /// <returns></returns>
        public async Task<List<FB_Thread>> fetchThreads(string thread_location, int? before = null, int? after = null, int? limit = null)
        {
            /*
             * Get all threads in thread_location.
             * Threads will be sorted from newest to oldest.
             * :param thread_location: ThreadLocation: INBOX, PENDING, ARCHIVED or OTHER
             * : param before: Fetch only thread before this epoch(in ms)(default all threads)
             * :param after: Fetch only thread after this epoch(in ms)(default all threads)
             * :param limit: The max. amount of threads to fetch(default all threads)
             * :return: `Thread` objects
             * :rtype: list:raises: FBchatException if request failed
             * */
            List<FB_Thread> threads = new List<FB_Thread>();
            string last_thread_timestamp = null;
            while (true)
            {
                // break if limit is exceeded
                if (limit != null && threads.Count >= limit)
                    break;
                // fetchThreadList returns at max 20 threads before last_thread_timestamp (included)
                var candidates = await this.fetchThreadList(
                    before: last_thread_timestamp, thread_location: thread_location
                );

                if (candidates.Count > 1)
                    threads.AddRange(candidates.Skip(1).ToList());
                else  // End of threads
                    break;

                last_thread_timestamp = threads.LastOrDefault()?.last_message_timestamp;

                // FB returns a sorted list of threads
                if ((before != null && long.Parse(last_thread_timestamp) > before) ||
                    (after != null && long.Parse(last_thread_timestamp) < after))
                    break;
            }

            // Return only threads between before and after (if set)
            if (after != null || before != null)
            {
                foreach (var t in threads)
                {
                    var last_message_timestamp = long.Parse(t.last_message_timestamp);
                    if ((before != null && last_message_timestamp > before) ||
                        (after != null && last_message_timestamp < after))
                        threads.Remove(t);
                }
            }

            if (limit != null && threads.Count > limit)
                return threads.Take((int)limit).ToList();

            return threads;
        }

        /// <summary>
        /// Get all users involved in threads.
        /// </summary>
        /// <param name="threads">threads: Thread: List of threads to check for users</param>
        /// <returns>`FB_User` objects</returns>
        public async Task<List<FB_User>> fetchAllUsersFromThreads(List<FB_Thread> threads)
        {
            /*
             * Get all users involved in threads.
             * :param threads: Thread: List of threads to check for users
             * :return: `User` objects
             * :rtype: list
             * :raises: FBchatException if request failed
             * */
            List<FB_User> users = new List<FB_User>();
            List<string> users_to_fetch = new List<string>();  // It's more efficient to fetch all users in one request

            foreach (var thread in threads)
            {
                if (thread is FB_User)
                {
                    if (!users.Select((u) => u.uid).Contains(thread.uid))
                        users.Add((FB_User)thread);
                }
                else if (thread is FB_Group)
                {
                    foreach (var user in ((FB_Group)thread).participants)
                    {
                        if (!users.Select((u) => u.uid).Contains(user.uid) &&
                            !users_to_fetch.Contains(user.uid))
                            users_to_fetch.Add(user.uid);
                    }
                }
            }

            foreach (KeyValuePair<string, FB_Thread> entry in await this.fetchThreadInfo(users_to_fetch))
                users.Add(entry.Value as FB_User);
            return users;
        }

        /// <summary>
        /// Fetch users the client is currently chatting with
        /// </summary>
        /// <returns>`FB_User` objects</returns>
        public async Task<List<FB_User>> fetchUsers()
        {
            /*
             * Fetch users the client is currently chatting with
             * This is very close to your friend list, with the follow differences:
             * It differs by including users that you're not friends with, but have chatted
             * with before, and by including accounts that are "Messenger Only".
             * But does not include deactivated, deleted or memorialized users (logically,
             * since you can't chat with those).
             * : return: `User` objects
             * :rtype: list
             * :raises: FBchatException if request failed
             * */

            var data = new Dictionary<string, object>() {
                { "viewer", this._uid },
            };
            var j = await this._session._payload_post("/chat/user_info_all", data: data);

            var users = new List<FB_User>();
            foreach (var u in j.Value<JObject>().Properties())
            {
                var k = u.Value;
                if (!new[] { "user", "friend" }.Contains(k?.get("type")?.Value<string>()) ||
                    new[] { "0", "\0" }.Contains(k.get("id").Value<string>()))
                {
                    // Skip invalid users
                    continue;
                }
                users.Add(FB_User._from_all_fetch(_session, k));
            }

            return users;
        }

        /// <summary>
        /// Find and get user by his/her name
        /// </summary>
        /// <param name="name">Name of the user</param>
        /// <param name="limit">The max. amount of users to fetch</param>
        /// <returns>`FB_User` objects, ordered by relevance</returns>
        public async Task<List<FB_User>> searchUsers(string name, int limit = 10)
        {
            /*
             * Find and get user by his/ her name
             * : param name: Name of the user
             * :param limit: The max. amount of users to fetch
             * : return: `User` objects, ordered by relevance
             * :rtype: list
             * :raises: FBchatException if request failed
             * */

            var param = new Dictionary<string, object>() {
                { "search", name }, { "limit", limit.ToString() }
             };
            var j = await this._session.graphql_request(GraphQL.from_query(GraphQL.SEARCH_USER, param));

            return j[name]?.get("users")?.get("nodes").Select(node => FB_User._from_graphql(_session, node)).ToList();
        }

        /// <summary>
        /// Find and get page by its name
        /// </summary>
        /// <param name="name">Name of the page</param>
        /// <param name="limit">The max. amount of pages to fetch</param>
        /// <returns>`FB_Page` objects, ordered by relevance</returns>
        public async Task<List<FB_Page>> searchPages(string name, int limit = 1)
        {
            /*
             * Find and get page by its name
             * : param name: Name of the page
             * :return: `Page` objects, ordered by relevance
             * :rtype: list
             * :raises: FBchatException if request failed
             * */

            var param = new Dictionary<string, object>() {
                { "search", name }, { "limit", limit.ToString() }
            };
            var j = await this._session.graphql_request(GraphQL.from_query(GraphQL.SEARCH_PAGE, param));

            return j[name]?.get("pages")?.get("nodes").Select(node => FB_Page._from_graphql(_session, node)).ToList();
        }

        /// <summary>
        /// Find and get group thread by its name
        /// </summary>
        /// <param name="name">Name of the group</param>
        /// <param name="limit">The max. amount of groups to fetch</param>
        /// <returns>`FB_Group` objects, ordered by relevance</returns>
        public async Task<List<FB_Group>> searchGroups(string name, int limit = 1)
        {
            /*
             * Find and get group thread by its name
             * :param name: Name of the group thread
             * :param limit: The max. amount of groups to fetch
             * :return: `Group` objects, ordered by relevance
             * :rtype: list
             * :raises: FBchatException if request failed
             * */

            var param = new Dictionary<string, object>() {
              { "search", name }, {"limit", limit.ToString() }
            };
            var j = await this._session.graphql_request(GraphQL.from_query(GraphQL.SEARCH_GROUP, param));

            return j.get("viewer")?.get("groups")?.get("nodes").Select(node => FB_Group._from_graphql(_session, node)).ToList();
        }

        /// <summary>
        /// Find and get a thread by its name
        /// </summary>
        /// <param name="name">Name of the thread</param>
        /// <param name="limit">The max. amount of threads to fetch</param>
        /// <returns>`FB_User`, `FB_Group` and `FB_Page` objects, ordered by relevance</returns>
        public async Task<List<FB_Thread>> searchThreads(string name, int limit = 1)
        {
            /*
             * Find and get a thread by its name
             * :param name: Name of the thread
             * :param limit: The max. amount of groups to fetch
             * : return: `User`, `Group` and `Page` objects, ordered by relevance
             * :rtype: list
             * :raises: FBchatException if request failed
             * */

            var param = new Dictionary<string, object>(){
                { "search", name }, {"limit", limit.ToString() }
            };
            var j = await this._session.graphql_request(GraphQL.from_query(GraphQL.SEARCH_THREAD, param));

            List<FB_Thread> rtn = new List<FB_Thread>();
            foreach (var node in j[name]?.get("threads")?.get("nodes"))
            {
                if (node.get("__typename").Value<string>().Equals("User"))
                {
                    rtn.Add(FB_User._from_graphql(_session, node));
                }
                else if (node.get("__typename").Value<string>().Equals("MessageThread"))
                {
                    // MessageThread => Group thread
                    rtn.Add(FB_Group._from_graphql(_session, node));
                }
                else if (node.get("__typename").Value<string>().Equals("Page"))
                {
                    rtn.Add(FB_Page._from_graphql(_session, node));
                }
                else if (node.get("__typename").Value<string>().Equals("Group"))
                {
                    // We don"t handle Facebook "Groups"
                    continue;
                }
                else
                {
                    Debug.WriteLine(string.Format("Unknown __typename: {0} in {1}", node.get("__typename").Value<string>(), node));
                }
            }

            return rtn;
        }

        /// <summary>
        /// Searches for messages in all threads
        /// Intended to be used alongside `FB_Thread.searchMessages`
        /// </summary>
        /// <param name="query">Text to search for</param>
        /// <param name="offset">Number of messages to skip</param>
        /// <param name="limit">Max. number of threads to retrieve</param>
        /// <returns>Iterable with tuples of threads, and the total amount of matching messages in each</returns>
        public async Task<List<(FB_Thread thread, int count)>> searchMessages(string query, int offset = 0, int limit = 5)
        {
            /*
             * Search for messages in all threads.
             * Intended to be used alongside `FB_Thread.searchMessages`
             * Warning! If someone send a message to a thread that matches the query, while
             * we're searching, some snippets will get returned twice.
             * Not sure if we should handle it, Facebook's implementation doesn't...
             * Args:
             *   query: Text to search for
             *   limit: Max. number of threads to retrieve. If ``None``, all threads will be
             *   retrieved.
             *   Returns:
             *     Iterable with tuples of threads, and the total amount of matching messages in each.
             */

            var data = new Dictionary<string, object>() {
                { "query", query },
                { "offset", offset.ToString() },
                { "limit", limit.ToString() }
            };
            var j = await this._session._payload_post("/ajax/mercury/search_snippets.php?dpr=1", data);
            var total_snippets = j?.get("search_snippets")?.get(query);

            var rtn = new List<(FB_Thread, int)>();
            foreach (var node in j?.get("graphql_payload")?.get("message_threads"))
            {
                FB_Thread thread = null;
                var type_ = node?.get("thread_type")?.Value<string>();
                if (type_ == "GROUP")
                    thread = new FB_Group(
                        session: _session, uid: node?.get("thread_key")?.get("thread_fbid")?.Value<string>()
                    );
                else if (type_ == "ONE_TO_ONE")
                    thread = new FB_Thread(
                        session: _session, uid: node?.get("thread_key")?.get("other_user_id")?.Value<string>()
                    );
                //if True:  // TODO: This check!
                // thread = UserData._from_graphql(self.session, node)
                //else:
                // thread = PageData._from_graphql(self.session, node)
                else
                {
                    throw new FBchatException(string.Format("Unknown thread type: {0}", type_));
                }
                if (thread != null)
                    rtn.Add((thread, total_snippets?.get(thread.uid)?.get("num_total_snippets")?.Value<int>() ?? 0));
                else
                    rtn.Add((null, 0));
            }
            return rtn;
        }

        private async Task<JObject> _fetchInfo(List<string> ids)
        {
            var data = new Dictionary<string, object>();
            foreach (var obj in ids.Select((x, index) => new { _id = x, i = index }))
                data.Add(string.Format("ids[{0}]", obj.i), obj._id);

            var j = await this._session._payload_post("/chat/user_info/", data);

            if (j.get("profiles") == null)
                throw new FBchatException("No users/pages returned");

            var entries = new JObject();
            foreach (var k in j.get("profiles")?.Value<JObject>()?.Properties())
            {
                if (new[] { "user", "friend" }.Contains(k.Value.get("type")?.Value<string>()))
                {
                    entries[k.Name] = new JObject() {
                        { "id", k.Name },
                        {"url", k.Value.get("uri")?.Value<string>() },
                        {"first_name", k.Value.get("firstName")?.Value<string>() },
                        {"is_viewer_friend", k.Value.get("is_friend")?.Value<bool>() ?? false },
                        {"gender", k.Value.get("gender")?.Value<string>() },
                        {"profile_picture", new JObject() { { "uri", k.Value.get("thumbSrc")?.Value<string>() } } },
                        { "name", k.Value.get("name")?.Value<string>() }
                    };
                }
                else if (k.Value.get("type").Value<string>().Equals("page"))
                {
                    entries[k.Name] = new JObject() {
                        { "id", k.Name},
                        { "url", k.Value.get("uri")?.Value<string>() },
                        { "profile_picture", new JObject() { { "uri", k.Value.get("thumbSrc")?.Value<string>() } } },
                        { "name", k.Value.get("name")?.Value<string>() }
                    };
                }
                else
                {
                    throw new FBchatException(string.Format("{0} had an unknown thread type: {1}", k.Name, k.Value));
                }
            }

            return entries;
        }

        /// <summary>
        /// Get logged user's info
        /// </summary>
        public async Task<FB_User> fetchProfile()
        {
            return (await this.fetchThreadInfo(new List<string>() { this._uid })).Single().Value as FB_User;
        }

        /// <summary>
        /// Get threads' info from IDs, unordered
        /// </summary>
        /// <param name="thread_ids">One or more thread ID(s) to query</param>
        /// <returns>A dictionary of FB_Thread objects, labeled by their ID</returns>
        public async Task<Dictionary<string, FB_Thread>> fetchThreadInfo(List<string> thread_ids)
        {
            /*
             * Get threads" info from IDs, unordered
             * ..warning::
             * Sends two requests if users or pages are present, to fetch all available info!
             * :param thread_ids: One or more thread ID(s) to query
             * :return: `models.Thread` objects, labeled by their ID
             * :rtype: dict
             * :raises: Exception if request failed
             */

            var queries = new List<GraphQL>();
            foreach (var thread_id in thread_ids)
            {
                queries.Add(GraphQL.from_doc_id(doc_id: "2147762685294928", param: new Dictionary<string, object>() {
                    { "id", thread_id },
                    { "message_limit", 0.ToString() },
                    { "load_messages", false.ToString() },
                    { "load_read_receipts", false.ToString() },
                    { "before", null }
                }));
            }

            var j = await this._session.graphql_requests(queries);

            foreach (var obj in j.Select((x, index) => new { entry = x, i = index }))
            {
                if (obj.entry.get("message_thread") == null)
                {
                    // If you don't have an existing thread with this person, attempt to retrieve user data anyways
                    j[obj.i]["message_thread"] = new JObject(
                        new JProperty("thread_key",
                            new JObject(
                                new JProperty("other_user_id", thread_ids[obj.i]))),
                        new JProperty("thread_type", "ONE_TO_ONE"));
                }
            }

            var pages_and_user_ids = j.Where(k => k.get("message_thread")?.get("thread_type")?.Value<string>()?.Equals("ONE_TO_ONE") ?? false)
                .Select(k => k.get("message_thread")?.get("thread_key")?.get("other_user_id")?.Value<string>());
            JObject pages_and_users = null;
            if (pages_and_user_ids.Count() != 0)
            {
                pages_and_users = await this._fetchInfo(pages_and_user_ids.ToList());
            }

            var rtn = new Dictionary<string, FB_Thread>();
            foreach (var obj in j.Select((x, index) => new { entry = x, i = index }))
            {
                var entry = obj.entry.get("message_thread");
                if (entry.get("thread_type")?.Value<string>()?.Equals("GROUP") ?? false)
                {
                    var _id = entry.get("thread_key")?.get("thread_fbid").Value<string>();
                    rtn[_id] = FB_Group._from_graphql(_session, entry);
                }
                if (entry.get("thread_type")?.Value<string>()?.Equals("MARKETPLACE") ?? false)
                {
                    var _id = entry.get("thread_key")?.get("thread_fbid").Value<string>();
                    rtn[_id] = FB_Marketplace._from_graphql(_session, entry);
                }
                else if (entry.get("thread_type")?.Value<string>()?.Equals("ONE_TO_ONE") ?? false)
                {
                    var _id = entry.get("thread_key")?.get("other_user_id")?.Value<string>();
                    if (pages_and_users[_id] == null)
                    {
                        throw new FBchatException(string.Format("Could not fetch thread {0}", _id));
                    }
                    foreach (var elem in pages_and_users[_id])
                    {
                        entry[((JProperty)elem).Name] = ((JProperty)elem).Value;
                    }
                    if (entry.get("first_name") != null)
                    {
                        rtn[_id] = FB_User._from_graphql(_session, entry);
                    }
                    else
                    {
                        rtn[_id] = FB_Page._from_graphql(_session, entry);
                    }
                }
                else
                {
                    throw new FBchatException(string.Format("{0} had an unknown thread type: {1}", thread_ids[obj.i], entry));
                }
            }

            return rtn;
        }

        /// <summary>
        /// Get thread list of your facebook account
        /// </summary>
        /// <param name="limit">Max.number of threads to retrieve. Capped at 20</param>
        /// <param name="thread_location">models.ThreadLocation: INBOX, PENDING, ARCHIVED or OTHER</param>
        /// <param name="before">A unix timestamp, indicating from which point to retrieve messages</param>
        public async Task<List<FB_Thread>> fetchThreadList(int limit = 20, string thread_location = ThreadLocation.INBOX, string before = null)
        {
            /*
             * Get thread list of your facebook account             
             * :param limit: Max.number of threads to retrieve.Capped at 20
             * :param thread_location: models.ThreadLocation: INBOX, PENDING, ARCHIVED or OTHER
             * :param before: A timestamp (in milliseconds), indicating from which point to retrieve threads
             * :type limit: int
             * :return: `models.Thread` objects
             * :rtype: list
             * :raises: Exception if request failed
             */

            if (limit > 20 || limit < 1)
            {
                throw new FBchatUserError("`limit` should be between 1 and 20");
            }

            var dict = new Dictionary<string, object>() {
                { "limit", limit },
                { "tags", new string[] { thread_location } },
                { "before", before },
                { "includeDeliveryReceipts", true },
                { "includeSeqID", false }
            };

            var j = await this._session.graphql_request(GraphQL.from_doc_id(doc_id: "1349387578499440", param: dict));

            var rtn = new List<FB_Thread>();
            foreach (var node in j.get("viewer")?.get("message_threads")?.get("nodes"))
            {
                var _type = node.get("thread_type")?.Value<string>();
                if (_type == "GROUP")
                    rtn.Add(FB_Group._from_graphql(_session, node));
                else if (_type == "ONE_TO_ONE")
                    rtn.Add(FB_User._from_thread_fetch(_session, node));
                else if (_type == "MARKETPLACE")
                    rtn.Add(FB_Marketplace._from_graphql(_session, node));
                else
                    throw new FBchatException(string.Format("Unknown thread type: {0}", _type));
            }
            return rtn;
        }

        /// <summary>
        /// Get unread user threads
        /// </summary>
        /// <returns>Returns unread thread ids</returns>
        public async Task<List<string>> fetchUnread()
        {
            /*
             * Get the unread thread list
             * :return: List of unread thread ids
             * :rtype: list
             * :raises: FBchatException if request failed
             */

            var form = new Dictionary<string, object>() {
                { "folders[0]", "inbox"},
                { "client", "mercury"},
                { "last_action_timestamp", (Utils.now() - 60 * 1000).ToString()},
                //{ "last_action_timestamp", 0.ToString()}
            };

            var j = await this._session._payload_post("/ajax/mercury/unread_threads.php", form);

            var result = j.get("unread_thread_fbids")?.FirstOrDefault();
            var rtn = new List<string>();
            rtn.AddRange(result?.get("thread_fbids")?.ToObject<List<string>>());
            rtn.AddRange(result?.get("other_user_fbids")?.ToObject<List<string>>());
            return rtn;
        }

        /// <summary>
        /// Get unseen user threads
        /// </summary>
        /// <returns>Returns unseen message ids</returns>
        public async Task<List<string>> fetchUnseen()
        {
            /*
             * Get the unseeen thread list
             * :return: List of unseeen thread ids
             * :rtype: list
             * :raises: FBchatException if request failed
             */

            var j = await this._session._payload_post("/mercury/unseen_thread_ids/", null);

            var result = j.get("unseen_thread_fbids")?.FirstOrDefault();
            var rtn = new List<string>();
            rtn.AddRange(result?.get("thread_fbids")?.ToObject<List<string>>());
            rtn.AddRange(result?.get("other_user_fbids")?.ToObject<List<string>>());
            return rtn;
        }

        /// <summary>
        /// Fetches the url to the original image from an image attachment ID
        /// </summary>
        /// <returns>An url where you can download the original image</returns>
        public async Task<string> fetchImageUrl(string image_id)
        {
            /*
             * Fetches the url to the original image from an image attachment ID
             * :param image_id: The image you want to fethc
             * :type image_id: str
             * : return: An url where you can download the original image
             * : rtype: str
             * : raises: FBChatException if request failed
             */

            var data = new Dictionary<string, object>() {
                { "photo_id", image_id},
            };
            var j = (JToken)await this._session._post("/mercury/attachments/photo/", data);

            var url = Utils.get_jsmods_require(j, 3);
            if (url == null)
                throw new FBchatException(string.Format("Could not fetch image url from: {0}", j));
            return url.Value<string>();
        }

        /// <summary>
        /// Gets friend active status as an `ActiveStatus` object.
        /// Returns ``null`` if status isn't known.
        /// .. warning::
        /// Only works when listening.
        /// </summary>
        /// <param name="user_id">ID of the user</param>
        /// <returns>Given user active status</returns>
        public FB_ActiveStatus getUserActiveStatus(string user_id)
        {
            /*
             * Gets friend active status as an `ActiveStatus` object.
             * Returns ``null`` if status isn't known.
             * .. warning::
             * Only works when listening.
             * :param user_id: ID of the user
             * :return: Given user active status
             * :rtype: ActiveStatus
             * */
            return this._buddylist.GetValueOrDefault(user_id);
        }

        /// <summary>
        /// Fetches currently active users
        /// </summary>
        /// <returns>List of active user ids</returns>
        public async Task<List<string>> fetchActiveUsers()
        {
            /*
             * Fetches currently active users
             * Also updates internal buddylist
             * :return: List of active user ids
             * :rtype: List
             * */
            var data = new Dictionary<string, object>()
            {
                { "data_fetch", true },
                { "send_full_data", true }
            };
            var j = await this._session._payload_post("https://m.facebook.com/buddylist_update.php", data);
            foreach (var buddy in j.get("buddylist"))
                this._buddylist[buddy.get("id")?.Value<string>()] = FB_ActiveStatus._from_buddylist_update(buddy);
            return j.get("buddylist")?.Select((b) => b.get("id")?.Value<string>())?.ToList();
        }

        #endregion

        #region MARK METHODS
        /// <summary>
        /// Mark a message as delivered
        /// </summary>
        /// <param name="thread_id">User/Group ID to which the message belongs.See :ref:`intro_threads`</param>
        /// <param name="message_id">Message ID to set as delivered.See :ref:`intro_threads`</param>
        /// <returns>true</returns>
        public async Task<bool> markAsDelivered(string thread_id, string message_id)
        {
            /*
             * Mark a message as delivered
             * :param thread_id: User/Group ID to which the message belongs.See :ref:`intro_threads`
             * :param message_id: Message ID to set as delivered.See :ref:`intro_threads`
             * :return: true
             * :raises: FBchatException if request failed
             * */
            var data = new Dictionary<string, object>{
            { "message_ids[0]", message_id },
            {string.Format("thread_ids[{0}][0]",thread_id), message_id}};

            var j = await this._session._payload_post("/ajax/mercury/delivery_receipts.php", data);
            return true;
        }

        private async Task _readStatus(bool read, List<string> thread_ids, long? timestamp = null)
        {
            var uthread_ids = Utils.require_list<string>(thread_ids);

            var data = new Dictionary<string, object> {
                { "watermarkTimestamp", timestamp ?? Utils.now() },
                { "shouldSendReadReceipt", "true" }
            };

            foreach (var thread_id in uthread_ids)
                data[string.Format("ids[{0}]", thread_id)] = read ? "true" : "false";

            var j = await this._session._payload_post("/ajax/mercury/change_read_status.php", data);
        }

        /// <summary>
        /// Mark threads as read
        /// All messages inside the threads will be marked as read
        /// </summary>
        /// <param name="thread_ids">User/Group IDs to set as read.See :ref:`intro_threads`</param>
        /// <param name="timestamp">Timestamp to signal the read cursor at, in milliseconds, default is now()</param>
        /// <returns></returns>
        public async Task markAsRead(List<string> thread_ids = null, long? timestamp = null)
        {
            /*
             * Mark threads as read
             * All messages inside the threads will be marked as read
             * :param thread_ids: User/Group IDs to set as read.See :ref:`intro_threads`
             * :raises: FBchatException if request failed
             * */
            await this._readStatus(true, thread_ids, timestamp);
        }

        /// <summary>
        /// Mark threads as unread
        /// All messages inside the threads will be marked as unread
        /// </summary>
        /// <param name="thread_ids">User/Group IDs to set as unread.See :ref:`intro_threads`</param>
        /// <param name="timestamp">Timestamp to signal the read cursor at, in milliseconds, default is now()</param>
        /// <returns></returns>
        public async Task markAsUnread(List<string> thread_ids = null, long? timestamp = null)
        {
            /*
             * Mark threads as unread
             * All messages inside the threads will be marked as unread
             * :param thread_ids: User/Group IDs to set as unread.See :ref:`intro_threads`
             * :raises: FBchatException if request failed
             * */
            await this._readStatus(false, thread_ids, timestamp);
        }

        public async Task markAsSeen()
        {
            /*
             * .. todo::
             * Documenting this
             * */
            var j = await this._session._payload_post("/ajax/mercury/mark_seen.php", new Dictionary<string, object>() { { "seen_timestamp", Utils.now() } });
        }

        /// <summary>
        /// Moves threads to specifed location
        /// </summary>
        /// <param name="location">ThreadLocation: INBOX, PENDING, ARCHIVED or OTHER</param>
        /// <param name="thread_ids">Thread IDs to move.See :ref:`intro_threads`</param>
        /// <returns>true</returns>
        public async Task<bool> moveThreads(string location, List<string> thread_ids)
        {
            /*
             * Moves threads to specifed location
             * :param location: ThreadLocation: INBOX, PENDING, ARCHIVED or OTHER
             * :param thread_ids: Thread IDs to move.See :ref:`intro_threads`
             * :return: true
             * :raises: FBchatException if request failed
             * */
            var uthread_ids = Utils.require_list<string>(thread_ids);

            if (location == ThreadLocation.PENDING)
                location = ThreadLocation.OTHER;

            if (location == ThreadLocation.ARCHIVED)
            {
                var data_archive = new Dictionary<string, object>();
                var data_unpin = new Dictionary<string, object>();
                foreach (string thread_id in uthread_ids)
                {
                    data_archive[string.Format("ids[{0}]", thread_id)] = "true";
                    data_unpin[string.Format("ids[{0}]", thread_id)] = "false";
                }
                var j_archive = await this._session._payload_post(
                    "/ajax/mercury/change_archived_status.php?dpr=1", data_archive
                );
                var j_unpin = await this._session._payload_post(
                    "/ajax/mercury/change_pinned_status.php?dpr=1", data_unpin
                );
            }
            else
            {
                var data = new Dictionary<string, object>();
                foreach (var obj in thread_ids.Select((x, index) => new { thread_id = x, i = index }))
                    data[string.Format("{0}[{1}]", location.ToLower(), obj.i)] = obj.thread_id;
                var j = await this._session._payload_post("/ajax/mercury/move_thread.php", data);
            }
            return true;
        }

        /// <summary>
        /// Deletes threads
        /// </summary>
        /// <param name="thread_ids">Thread IDs to delete. See :ref:`intro_threads`</param>
        /// <returns>true</returns>
        public async Task<bool> deleteThreads(List<string> thread_ids)
        {
            /*
             * Deletes threads
             * :param thread_ids: Thread IDs to delete. See :ref:`intro_threads`
             * :return: true
             * :raises: FBchatException if request failed
             * */
            var uthread_ids = Utils.require_list<string>(thread_ids);

            var data_unpin = new Dictionary<string, object>();
            var data_delete = new Dictionary<string, object>();
            foreach (var obj in thread_ids.Select((x, index) => new { thread_id = x, i = index }))
            {
                data_unpin[string.Format("ids[{0}]", obj.thread_id)] = "false";
                data_delete[string.Format("ids[{0}]", obj.i)] = obj.thread_id;
            }
            var j_unpin = await this._session._payload_post(
                "/ajax/mercury/change_pinned_status.php?dpr=1", data_unpin
            );
            var j_delete = this._session._payload_post(
                "/ajax/mercury/delete_thread.php?dpr=1", data_delete
            );
            return true;
        }

        /// <summary>
        /// Deletes specifed messages
        /// </summary>
        /// <param name="message_ids">Message IDs to delete</param>
        /// <returns>true</returns>
        public async Task<bool> deleteMessages(List<string> message_ids)
        {
            /*
             * Deletes specifed messages
             * :param message_ids: Message IDs to delete
             * :return: true
             * :raises: FBchatException if request failed
             * */
            var umessage_ids = Utils.require_list<string>(message_ids);
            var data = new Dictionary<string, object>();
            foreach (var obj in umessage_ids.Select((x, index) => new { message_id = x, i = index }))
                data[string.Format("message_ids[{0}]", obj.i)] = obj.message_id;
            var j = await this._session._payload_post("/ajax/mercury/delete_messages.php?dpr=1", data);
            return true;
        }

        #endregion

        #region LISTEN METHODS

        private async Task _ping(CancellationToken cancellationToken = default(CancellationToken))
        {
            var data = new Dictionary<string, object>() {
                { "seq", this._sequence_id },
                { "channel", "p_" + this._uid },
                { "clientid", this._session.get_client_id() },
                { "partition", -2 },
                { "cap", 0 },
                { "uid", this._uid },
                { "sticky_token", this._sticky },
                { "sticky_pool", this._pool },
                { "viewer_uid", this._uid },
                { "state", "active" },
            };
            var j = await this._session._get(
                string.Format("https://{0}-edge-chat.facebook.com/active_ping", this._pull_channel), data, cancellationToken);
        }

        private async Task<JToken> _pullMessage(CancellationToken cancellationToken = default(CancellationToken))
        {
            /*Call pull api with seq value to get message data.*/
            var data = new Dictionary<string, object>() {
                { "seq", this._sequence_id },
                { "msgs_recv", 0 },
                { "sticky_token", this._sticky },
                { "sticky_pool", this._pool },
                { "clientid", this._session.get_client_id() },
                { "state", this._markAlive ? "active" : "offline" },
            };

            return await this._session._get(
                string.Format("https://{0}-edge-chat.facebook.com/pull", this._pull_channel), data, cancellationToken);
        }

        private async Task _parseDelta(JToken m)
        {
            var delta = m.get("delta");            
            
            // Client payload (that weird numbers)
            if (delta.get("class")?.Value<string>() == "ClientPayload")
            {
                foreach (var ev in ClientPayload.parse_client_payloads(this._session, delta) ?? Enumerable.Empty<FB_Event>())
                {
                    await this.onEvent(ev);
                }
            }
            else if (delta.get("class")?.Value<string>() != null)
            {
                var ev = DeltaClass.parse_delta(_session, delta);
                if (ev != null)
                    await this.onEvent(ev);
            }
            else if (delta.get("type")?.Value<string>() != null)
            {
                var ev = DeltaType.parse_delta(_session, delta);
                if (ev != null)
                    await this.onEvent(ev);
            }
            // Unknown message type
            else
                await this.onUnknownMesssageType(msg: m);
        }

        private async Task _parseMessage(JToken content)
        {
            /*Get message and author name from content. May contain multiple messages in the content.*/
            this._sequence_id = content.get("seq")?.Value<int>() ?? _sequence_id;

            if (content.get("lb_info") != null)
            {
                this._sticky = content.get("lb_info")?.get("sticky")?.Value<string>();
                this._pool = content.get("lb_info")?.get("pool")?.Value<string>();
            }

            if (content.get("batches") != null)
            {
                foreach (var batch in content.get("batches"))
                    await this._parseMessage(batch);
            }

            if (content.get("ms") == null) return;

            foreach (var m in content.get("ms"))
            {
                var mtype = m.get("type").Value<string>();
                try
                {
                    // Things that directly change chat
                    if (mtype == "delta")
                    {
                        await this._parseDelta(m);
                    }
                    // Inbox
                    else if (mtype == "inbox")
                    {
                        await this.onInbox(unseen: m.get("unseen").Value<int>(), unread: m.get("unread").Value<int>(), recent_unread: m.get("recent_unread").Value<int>(), msg: m);
                    }
                    // Typing
                    else if (mtype == "typ" || mtype == "ttyp")
                    {
                        var author_id = m.get("from")?.Value<string>();
                        var thread_id = m.get("thread_fbid")?.Value<string>();
                        FB_Thread thread = null;
                        if (thread_id != null)
                        {
                            thread = new FB_Group(thread_id, _session);
                        }
                        else
                        {
                            if (author_id == this._uid)
                                thread_id = m.get("to")?.Value<string>();
                            else
                                thread_id = author_id;
                            thread = new FB_User(thread_id, _session);
                        }
                        var typing_status = m.get("st")?.Value<int>() == 1;
                        await this.onTyping(
                            author_id: author_id,
                            status: typing_status,
                            thread: thread,
                            msg: m
                        );
                    }
                    // Delivered

                    // Seen
                    //else if (mtype == "m_read_receipt":
                    //
                    // this.onSeen(m.get('realtime_viewer_fbid'), m.get('reader'), m.get('time'))

                    else if (mtype == "jewel_requests_add")
                    {
                        var from_id = m.get("from")?.Value<string>();
                        await this.onFriendRequest(from_id: from_id, msg: m);
                    }

                    // Happens on every login
                    else if (mtype == "qprimer")
                        await this.onQprimer(ts: m.get("made")?.Value<long>() ?? 0, msg: m);

                    // Is sent before any other message
                    else if (mtype == "deltaflow")
                    { }

                    // Chat timestamp
                    else if (mtype == "chatproxy-presence")
                    {
                        var statuses = new Dictionary<string, FB_ActiveStatus>();
                        if (m.get("buddyList") != null)
                        {
                            foreach (var payload in m.get("buddyList").Value<JObject>().Properties())
                            {
                                statuses[payload.Name] = FB_ActiveStatus._from_chatproxy_presence(payload.Name, payload.Value);
                                this._buddylist[payload.Name] = statuses[payload.Name];
                            }
                            await this.onChatTimestamp(buddylist: statuses, msg: m);
                        }
                    }

                    // Buddylist overlay
                    else if (mtype == "buddylist_overlay")
                    {
                        var statuses = new Dictionary<string, FB_ActiveStatus>();
                        if (m.get("overlay") != null)
                        {
                            foreach (var payload in m.get("overlay").Value<JObject>().Properties())
                            {
                                bool old_in_game = false;
                                if (this._buddylist.ContainsKey(payload.Name))
                                    old_in_game = this._buddylist[payload.Name].in_game;

                                statuses[payload.Name] = FB_ActiveStatus._from_buddylist_overlay(
                                    payload.Value, old_in_game
                                );
                                this._buddylist[payload.Name] = statuses[payload.Name];
                            }
                            await this.onBuddylistOverlay(statuses: statuses, msg: m);
                        }
                        // Unknown message type
                        else
                        {
                            await this.onUnknownMesssageType(msg: m);
                        }
                    }
                }
                catch (Exception ex)
                {
                    await this.onMessageError(exception: ex, msg: m);
                }
            }
        }

        private async Task<int> _fetch_mqtt_sequence_id()
        {
            // Get the sync sequence ID used for the /messenger_sync_create_queue call later.
            // This is the same request as fetch_thread_list, but with includeSeqID=true
            var j = await this._session.graphql_request(GraphQL.from_doc_id("1349387578499440", new Dictionary<string, object> {
                { "limit", 1 },
                { "tags", new string[] {ThreadLocation.INBOX } },
                { "before", null },
                { "includeDeliveryReceipts", false },
                { "includeSeqID", true },
            }));

            var sequence_id = j.get("viewer")?.get("message_threads")?.get("sync_sequence_id")?.Value<int>();
            if (sequence_id == null)
                throw new FBchatException("Could not fetch sequence id");
            return (int)sequence_id;
        }

        /// <summary>
        /// Start listening from an external event loop
        /// </summary>
        /// <param name="_cancellationTokenSource"></param>
        /// <param name="markAlive">Whether this should ping the Facebook server before running</param>
        public async Task<bool> startListening(CancellationTokenSource _cancellationTokenSource, bool markAlive = true)
        {
            /*
             * Start listening from an external event loop
             * :raises: Exception if request failed
             */

            this._markAlive = markAlive;

            var factory = new MqttFactory();
            if (this.mqttClient != null)
            {
                this.mqttClient.UseDisconnectedHandler((e) => { });
                try { await this.mqttClient.DisconnectAsync(); }
                catch { }
                this.mqttClient.Dispose();
                this.mqttClient = null;
            }
            this.mqttClient = factory.CreateMqttClient();

            mqttClient.UseConnectedHandler(async e =>
            {
                Debug.WriteLine("MQTT: connected with server");

                // Subscribe to a topic
                await mqttClient.SubscribeAsync(
                    new TopicFilterBuilder().WithTopic("/legacy_web").Build(),
                    new TopicFilterBuilder().WithTopic("/webrtc").Build(),
                    new TopicFilterBuilder().WithTopic("/br_sr").Build(),
                    new TopicFilterBuilder().WithTopic("/sr_res").Build(),
                    new TopicFilterBuilder().WithTopic("/t_ms").Build(), // Messages
                    new TopicFilterBuilder().WithTopic("/thread_typing").Build(), // Group typing notifications
                    new TopicFilterBuilder().WithTopic("/orca_typing_notifications").Build(), // Private chat typing notifications
                    new TopicFilterBuilder().WithTopic("/thread_typing").Build(),
                    new TopicFilterBuilder().WithTopic("/notify_disconnect").Build(),
                    new TopicFilterBuilder().WithTopic("/orca_presence").Build());

                // I read somewhere that not doing this might add message send limits
                await mqttClient.UnsubscribeAsync("/orca_message_notifications");
                await this._messenger_queue_publish();

                Debug.WriteLine("MQTT: subscribed");
            });

            mqttClient.UseApplicationMessageReceivedHandler(async e =>
            {
                Debug.WriteLine("MQTT: received application message");
                Debug.WriteLine($"+ Topic = {e.ApplicationMessage.Topic}");
                Debug.WriteLine($"+ Payload = {Encoding.UTF8.GetString(e.ApplicationMessage.Payload)}");
                Debug.WriteLine($"+ QoS = {e.ApplicationMessage.QualityOfServiceLevel}");

                var event_type = e.ApplicationMessage.Topic;
                var data = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                try
                {
                    var event_data = Utils.to_json(data);
                    await this._try_parse_mqtt(event_type, event_data);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.ToString());
                }
            });

            mqttClient.UseDisconnectedHandler(async e =>
            {
                Debug.WriteLine("MQTT: disconnected from server");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _cancellationTokenSource.Token);
                    await mqttClient.ConnectAsync(_get_connect_options(), _cancellationTokenSource.Token);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(ex.ToString());
                }
            });

            await mqttClient.ConnectAsync(_get_connect_options(), _cancellationTokenSource.Token);

            this.listening = true;
            return this.listening;
        }

        private async Task _messenger_queue_publish()
        {
            this._mqtt_sequence_id = await _fetch_mqtt_sequence_id();

            var payload = new Dictionary<string, object>(){
                        { "sync_api_version", 10 },
                        { "max_deltas_able_to_process", 1000 },
                        { "delta_batch_size", 500 },
                        { "encoding", "JSON" },
                        { "entity_fbid", this._uid }
                };

            if (this._sync_token == null)
            {
                Debug.WriteLine("MQTT: sending messenger sync create queue request");
                var message = new MqttApplicationMessageBuilder()
                .WithTopic("/messenger_sync_create_queue")
                .WithPayload(JsonConvert.SerializeObject(
                    new Dictionary<string, object>(payload) {
                        { "initial_titan_sequence_id", this._mqtt_sequence_id.ToString() },
                        { "device_params", null }
                    })).Build();
                await mqttClient.PublishAsync(message);
            }
            else
            {
                Debug.WriteLine("MQTT: sending messenger sync get diffs request");
                var message = new MqttApplicationMessageBuilder()
                .WithTopic("/messenger_sync_get_diffs")
                .WithPayload(JsonConvert.SerializeObject(
                    new Dictionary<string, object>(payload) {
                        { "last_seq_id", this._mqtt_sequence_id.ToString() },
                        { "sync_token", this._sync_token }
                    })).Build();
                await mqttClient.PublishAsync(message);
            }
        }

        private IMqttClientOptions _get_connect_options()
        {
            // Random session ID
            var sid = new Random().Next(1, int.MaxValue);

            // The MQTT username. There's no password.
            var username = new Dictionary<string, object>() {
                { "u", this._uid }, // USER_ID
                { "s", sid },
                { "cp", 3 }, // CAPABILITIES
                { "ecp", 10 }, // ENDPOINT_CAPABILITIES
                { "chat_on", this._markAlive }, // MAKE_USER_AVAILABLE_IN_FOREGROUND
                { "fg", this._markAlive }, // INITIAL_FOREGROUND_STATE
                // Not sure if this should be some specific kind of UUID, but it's a random one now.
                { "d", Guid.NewGuid().ToString() },
                { "ct", "websocket" }, // CLIENT_TYPE
                { "mqtt_sid", "" }, // CLIENT_MQTT_SESSION_ID
                // Application ID, taken from facebook.com
                { "aid", 219994525426954 },
                { "st", new string[0] }, // SUBSCRIBE_TOPICS
                { "pm", new string[0] },
                { "dc", "" },
                { "no_auto_fg", true }, // NO_AUTOMATIC_FOREGROUND
                { "gas", null }
            };

            // Headers for the websocket connection. Not including Origin will cause 502's.
            // User agent and Referer also probably required. Cookie is how it auths.
            // Accept is there just for fun.
            var cookies = this._session.get_cookies();

            var headers = new Dictionary<string, string>() {
                { "Referer", "https://www.facebook.com" },
                { "User-Agent", Utils.USER_AGENTS[0] },
                { "Cookie", string.Join(";", cookies[".facebook.com"].Select(c => $"{c.Name}={c.Value}"))},
                { "Accept", "*/*"},
                { "Origin", "https://www.messenger.com" }
            };

            // Use WebSocket connection.
            var options = new MqttClientOptionsBuilder()
                        .WithClientId("mqttwsclient")
                        .WithWebSocketServer($"wss://edge-chat.facebook.com/chat?region=lla&sid={sid}",
                            new MqttClientOptionsBuilderWebSocketParameters() { RequestHeaders = headers })
                        .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V310)
                        .WithCredentials(JsonConvert.SerializeObject(username), "")
                        .Build();
            return options;
        }

        private async Task _try_parse_mqtt(string event_type, JToken event_data)
        {
            try
            {
                await this._parse_mqtt(event_type, event_data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
            }
        }

        private async Task _parse_mqtt(string event_type, JToken event_data)
        {
            if (event_type == "/t_ms")
            {
                if (event_data.get("errorCode") != null)
                {
                    Debug.WriteLine(string.Format("MQTT error: {0}", event_data.get("errorCode")?.Value<string>()));
                    this._sync_token = null;
                    await this._messenger_queue_publish();
                }
                else
                {
                    // Update sync_token when received
                    // This is received in the first message after we've created a messenger
                    // sync queue.
                    if (event_data?.get("syncToken") != null && event_data?.get("firstDeltaSeqId") != null)
                    {
                        this._sync_token = event_data?.get("syncToken")?.Value<string>();
                        this._mqtt_sequence_id = event_data?.get("firstDeltaSeqId")?.Value<int>() ?? _mqtt_sequence_id;
                    }

                    // Update last sequence id when received
                    if (event_data?.get("lastIssuedSeqId") != null)
                    {
                        this._mqtt_sequence_id = event_data?.get("lastIssuedSeqId")?.Value<int>() ?? _mqtt_sequence_id;
                        //this._mqtt_sequence_id = Math.Max(this._mqtt_sequence_id,
                        //    event_data.get("lastIssuedSeqId")?.Value<int>() ?? event_data.get("deltas")?.LastOrDefault()?.get("irisSeqId")?.Value<int>() ?? _mqtt_sequence_id);
                    }

                    foreach (var delta in event_data.get("deltas") ?? Enumerable.Empty<JToken>())
                        await this._parseDelta(new JObject() { { "delta", delta } });
                }
            }
            else if (new string[] { "/thread_typing", "/orca_typing_notifications" }.Contains(event_type))
            {
                var author_id = event_data.get("sender_fbid")?.Value<string>();
                var thread_id = event_data.get("thread")?.Value<string>() ?? author_id;
                var typing_status = event_data.get("state")?.Value<int>() == 1;
                await this.onTyping(
                    author_id: author_id,
                    status: typing_status,
                    thread: thread_id == author_id ? (FB_Thread)new FB_User(thread_id, _session) : (FB_Thread)new FB_Group(thread_id, _session),
                    msg: event_data
                );
            }
            else if (event_type == "/orca_presence")
            {
                var statuses = new Dictionary<string, FB_ActiveStatus>();
                foreach (var data in event_data.get("list"))
                {
                    var user_id = data["u"]?.Value<string>();

                    bool old_in_game = false;
                    if (this._buddylist.ContainsKey(user_id))
                        old_in_game = this._buddylist[user_id].in_game;

                    statuses[user_id] = FB_ActiveStatus._from_orca_presence(data, old_in_game);
                    this._buddylist[user_id] = statuses[user_id];

                    await this.onBuddylistOverlay(statuses: statuses, msg: event_data);
                }
            }
            else if (event_type == "/legacy_web")
            {
                // Friend request
                if (event_data?.get("type")?.Value<string>() == "jewel_requests_add")
                {
                    var from_id = event_data?.get("from")?.Value<string>();
                    await this.onFriendRequest(from_id: from_id, msg: event_data);
                }
            }
        }

        /// <summary>
        /// Does one cycle of the listening loop.
        /// This method is useful if you want to control fbchat from an external event loop
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns>Whether the loop should keep running</returns>
        public async Task<bool> doOneListen(CancellationToken cancellationToken = default(CancellationToken))
        {
            /*
             * Does one cycle of the listening loop.
             * This method is useful if you want to control fbchat from an external event loop
             * :return: Whether the loop should keep running
             * :rtype: bool
             */

            try
            {
                if (this._markAlive) await this._ping(cancellationToken);
                var content = await this._pullMessage(cancellationToken);
                if (content != null) await this._parseMessage(content);
            }
            catch (FBchatFacebookError ex)
            {
                if (new int[] { 502, 503 }.Contains(ex.request_status_code))
                {
                    // Bump pull channel, while contraining withing 0-4
                    this._pull_channel = (this._pull_channel + 1) % 5;
                }
                else
                {
                    throw (ex);
                }
            }
            catch (Exception ex)
            {
                return await this.onListenError(exception: ex);
            }

            return true;
        }

        /// <summary>
        /// Cleans up the variables from startListening
        /// </summary>
        public async Task stopListening()
        {
            // Stop mqtt client
            if (this.mqttClient != null)
            {
                this.mqttClient.UseDisconnectedHandler((e) => { });
                try { await this.mqttClient.DisconnectAsync(); }
                catch { }
                this.mqttClient.Dispose();
                this.mqttClient = null;
            }

            // Cleans up the variables from startListening
            this.listening = false;
            this._sticky = null;
            this._pool = null;
            this._sync_token = null;
        }

        /// <summary>
        /// Changes client active status while listening
        /// </summary>
        /// <param name="markAlive">Whether to show if client is active</param>
        public async void setActiveStatus(bool markAlive)
        {
            /*
             * Changes client active status while listening
             * :param markAlive: Whether to show if client is active
             * :type markAlive: bool
             * */
            if (this._markAlive != markAlive)
            {
                this._markAlive = markAlive;
                if (this.mqttClient != null && this.mqttClient.IsConnected)
                    await this.mqttClient.DisconnectAsync(); // Need to disconnect and connect again
            }
        }
        #endregion

        #region EVENTS
        /// <summary>
        /// Called when the client is logging in
        /// </summary>
        /// <param name="email">The email of the client</param>
        protected virtual async Task onLoggingIn(string email = null)
        {
            /*
             * Called when the client is logging in
             * :param email: The email of the client
             * */
            Debug.WriteLine(string.Format("Logging in {0}...", email));
            await Task.Yield();
        }

        /// <summary>
        /// Called when a 2FA code is requested
        /// </summary>
        protected virtual async Task<string> on2FACode()
        {
            /*
             * Called when a 2FA code is requested
             */
            await Task.Yield();
            throw new NotImplementedException("You should override this.");
        }

        /// <summary>
        /// Called when the client is successfully logged in
        /// </summary>
        /// <param name="email">The email of the client</param>
        protected virtual async Task onLoggedIn(string email = null)
        {
            /*
             * Called when the client is successfully logged in
             * :param email: The email of the client
             * */
            Debug.WriteLine(string.Format("Login of {0} successful.", email));
            await Task.Yield();
        }

        /// <summary>
        /// Called when the client is listening
        /// </summary>
        protected virtual async Task onListening()
        {
            /*
             * Called when the client is listening
             * */
            Debug.WriteLine("Listening...");
            await Task.Yield();
        }

        /// <summary>
        /// Called when an error was encountered while listening
        /// </summary>
        /// <param name="exception">The exception that was encountered</param>
        protected virtual async Task<bool> onListenError(Exception exception = null)
        {
            /*
             * Called when an error was encountered while listening
             * :param exception: The exception that was encountered
             */
            Debug.WriteLine(string.Format("Got exception while listening: {0}", exception));
            return await Task.FromResult(true);
        }

        /// <summary>
        /// Called when an error was encountered while listening on mqtt
        /// </summary>
        /// <param name="exception">The exception that was encountered</param>
        public virtual async Task onMqttListenError(Exception exception = null)
        {
            /*
             * Called when an error was encountered while listening on mqtt
             * :param exception: The exception that was encountered
             */
            Debug.WriteLine(string.Format("Got mqtt exception while listening: {0}", exception));
            await Task.Yield();
        }

        /// <summary>
        /// Called when the client is listening, and an event happens.
        /// </summary>
        /// <param name="ev"></param>
        /// <returns></returns>
        protected virtual async Task onEvent(FB_Event ev)
        {
            /*Called when the client is listening, and an event happens.*/
            Debug.WriteLine("Got event: {0}", ev);
            await Task.Yield();
        }        

        /// <summary>
        /// Called when the client is listening, and somebody that isn't
        /// connected with you on either Facebook or Messenger sends a message.
        /// After that, you need to use fetchThreadList to actually read the message.
        /// </summary>
        /// <param name="thread">Thread that the message was sent to</param>
        /// <param name="metadata">Extra metadata about the message</param>
        /// <param name="msg">A full set of the data received</param>
        /// <returns></returns>
        protected virtual async Task onPendingMessage(FB_Thread thread = null, JToken metadata = null, JToken msg = null)
        {
            /*
             * Called when the client is listening, and somebody that isn't
             * connected with you on either Facebook or Messenger sends a message.
             * After that, you need to use fetchThreadList to actually read the message.
             * Args:
             *   thread: Thread that the message was sent to. See: ref:`intro_threads`
             *   metadata: Extra metadata about the message
             *   msg: A full set of the data received
             */
            Debug.WriteLine(string.Format("New pending message from {0}", thread.uid));
            await Task.Yield();
        }

        ///<summary>
        /// Called when the client is listening, and somebody sends a friend request
        ///</summary>
        /// <param name="from_id">The ID of the person that sent the request</param>
        /// <param name="msg">A full set of the data received</param>
        protected virtual async Task onFriendRequest(object from_id = null, JToken msg = null)
        {
            Debug.WriteLine(string.Format("Friend request from {0}", from_id));
            await Task.Yield();
        }

        ///<summary>
        /// .. todo::
        /// Documenting this
        ///</summary>
        /// <param name="unseen">--</param>
        /// <param name="unread">--</param>
        /// <param name="recent_unread">--</param>
        /// <param name="msg">A full set of the data received</param>
        protected virtual async Task onInbox(
            object unseen = null,
            object unread = null,
            object recent_unread = null,
            JToken msg = null)
        {
            Debug.WriteLine(string.Format("Inbox event: {0}, {1}, {2}", unseen, unread, recent_unread));
            await Task.Yield();
        }

        ///<summary>
        /// Called when the client is listening, and somebody starts or stops typing into a chat
        ///</summary>
        /// <param name="author_id">The ID of the person who sent the action</param>
        /// <param name="status">The typing status: true is typing, false if not.</param>
        /// <param name="thread">Thread that the action was sent to. See :ref:`intro_threads`</param>
        /// <param name="msg">A full set of the data received</param>
        protected virtual async Task onTyping(
            string author_id = null,
            object status = null,
            FB_Thread thread = null,
            JToken msg = null)
        {
            await Task.Yield();
        }

        ///<summary>
        /// Called when the client just started listening
        ///</summary>
        /// <param name="ts">A timestamp of the action</param>
        /// <param name="msg">A full set of the data received</param>
        protected virtual async Task onQprimer(long ts = 0, JToken msg = null)
        {
            await Task.Yield();
        }

        ///<summary>
        /// Called when the client receives chat online presence update
        ///</summary>
        /// <param name="buddylist">A list of dicts with friend id and last seen timestamp</param>
        /// <param name="msg">A full set of the data received</param>
        protected virtual async Task onChatTimestamp(object buddylist = null, JToken msg = null)
        {
            Debug.WriteLine(string.Format("Chat Timestamps received: {0}", buddylist));
            await Task.Yield();
        }

        ///<summary>
        /// Called when the client is listening and client receives information about friend active status
        ///</summary>
        /// <param name="statuses">Dictionary with user IDs as keys and `ActiveStatus` as values</param>
        /// <param name="msg">A full set of the data received</param>
        protected virtual async Task onBuddylistOverlay(object statuses = null, JToken msg = null)
        {
            Debug.WriteLine(string.Format("Buddylist overlay received: {0}", statuses));
            await Task.Yield();
        }

        ///<summary>
        /// Called when the client is listening, and some unknown data was received
        ///</summary>
        /// <param name="msg">A full set of the data received</param>
        protected virtual async Task onUnknownMesssageType(JToken msg = null)
        {
            Debug.WriteLine(string.Format("Unknown message received: {0}", msg));
            await Task.Yield();
        }

        ///<summary>
        /// Called when an error was encountered while parsing received data
        ///</summary>
        /// <param name="exception">The exception that was encountered</param>
        /// <param name="msg">A full set of the data received</param>
        protected virtual async Task onMessageError(object exception = null, JToken msg = null)
        {
            Debug.WriteLine(string.Format("Exception in parsing of {0}", msg));
            await Task.Yield();
        }

        #endregion

        /// <returns>Pretty string representation of the client</returns>
        public override string ToString()
        {
            return this.__unicode__();
        }

        private string __unicode__()
        {
            return string.Format("<Client(session={0})>", this._session.ToString());
        }
    }
}
