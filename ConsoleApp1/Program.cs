using System;
using System.Collections.Generic;
using System.Text;
using System.Data.SQLite;
using Newtonsoft.Json.Linq;
using System.Net;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace ManifestLoader
{
    class manifester
    {
        public static void Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("Please provide api key and output directory.");
                return;
            }
            var j = Manifest.LoadManifest(args[0]).ManifestData;
            File.WriteAllText(args[1], j);
        }
    }

    public class Manifest
    {
        /// <summary>
        /// string respresentative of the bungie Manifest
        /// </summary>
        public string ManifestData { get; set; }

        private Random Random;
        /// <summary>
        /// static method to fetch the latest manifest file from bungie, unzip it, and pass back a populated Manifest class
        /// </summary>
        /// <returns>Manifest class that represents the current "mobileWorldContentPaths" manifest</returns>
        public static Manifest LoadManifest(string apiKey)
        {
            //get the manifest details
            string requestURL = "https://www.bungie.net/Platform/Destiny2/Manifest";
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestURL);
            request.Headers.Add("X-API-Key", apiKey); //not currently required
            request.KeepAlive = true;
            request.Host = "www.bungie.net";
            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            string responseText;
            using (TextReader tr = new StreamReader(response.GetResponseStream()))
            {
                responseText = tr.ReadToEnd();
            }
            dynamic json = JObject.Parse(responseText);
            //find and delete the existing manifest if it already exists - you probably don't want to do this, but this is throwaway ;)
            string manifestPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
           string jsonVersion = json.Response.version;
            string manifestFile = Path.Combine(manifestPath, jsonVersion + ".manifest");
            if (File.Exists(manifestFile))
                File.Delete(manifestFile);
            try
            {
                //get the current manifest file stream
                string manifestURL = json.Response.mobileWorldContentPaths.en;
                request = (HttpWebRequest)WebRequest.Create("https://www.bungie.net" + manifestURL);
                using (Stream stream = request.GetResponse().GetResponseStream())
                {
                    //need to use a memory stream as the Microsoft ZipArchive doesn't seem to like non-seekable streams
                    using (MemoryStream ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        ms.Seek(0, SeekOrigin.Begin);
                        //extract the manifest file from it's zip container
                        ZipArchive zippedManifest = new ZipArchive(ms);
                        zippedManifest.Entries[0].ExtractToFile(manifestFile);
                    }
                }
                //time to turn the manifest into a class!
                return new Manifest(manifestFile);
            }
            catch (Exception ex)
            {
                throw;
            }
        }
        //used to map the database to json
        public struct ManifestBranch
        {
            public string TableName;
            public List<dynamic> Json;
            public List<string> JsonString;
        }
        /// <summary>
        /// where the magic happens...
        /// Takes a SQLite manifest file, and returns a Manifest class with all the data organised into queriable json.
        /// </summary>
        /// <param name="manifestFile">the bungie manifest SQLite database file</param>
        public Manifest(string manifestFile)
        {
            Random = new Random();

            using (var sqConn = new SQLiteConnection("Data Source=" + manifestFile + ";Version=3;"))
            {
                sqConn.Open();
                //build a list of all the tables
                List<string> tableNames = new List<string>();
                using (var sqCmd = new SQLiteCommand("SELECT name FROM sqlite_master WHERE type='table'", sqConn))
                {
                    using (var sqReader = sqCmd.ExecuteReader())
                    {
                        while (sqReader.Read())
                            tableNames.Add((string)sqReader["name"]);
                    }
                }
                //get the json for each row, in each table, and store it in a lovely array
                List<ManifestBranch> manifestData = new List<ManifestBranch>();
                foreach (var tableName in tableNames)
                {
                    var manifestBranch = new ManifestBranch { TableName = tableName, Json = new List<dynamic>(), JsonString = new List<string>() };
                    using (var sqCmd = new SQLiteCommand("SELECT * FROM " + tableName, sqConn))
                    {
                        using (var sqReader = sqCmd.ExecuteReader())
                        {
                            while (sqReader.Read())
                            {
                                byte[] jsonData = (byte[])sqReader["json"];
                                string jsonString = Encoding.ASCII.GetString(jsonData);
                                manifestBranch.JsonString.Add(Entry(jsonString));
                            }
                        }
                    }
                    manifestData.Add(manifestBranch);
                }
                //this next bit takes all of the json, for all of the tables, and wraps it up nicely
                //into a single json string, which is then made dynamic and thus queriable ^_^
                string fullJson = "{";
                foreach (var manifestBranch in manifestData)
                {
                    fullJson += "\"" + manifestBranch.TableName + "\":{" + string.Join(",", manifestBranch.JsonString) + "},";
                }
                fullJson = fullJson.TrimEnd(',') + "}";

                ManifestData = fullJson;

                //instead of the above, you can just loop through each branch and create individual files to directly replicate lowlines code example
                //however, it's probably best to do this further up, where we're looping through each table, rather than down here at the end ;)
                sqConn.Close();
            }
        }

        private string Entry(string jsonString)
        {
            var hash = (string)JObject.Parse(jsonString)["hash"];
            if(String.IsNullOrEmpty(hash))
            {
                // generate random number as top level key for consistent format
                hash = String.Format("g{0}", Random.Next(10000, 99999));
            }
            return String.Format("\"{0}\":{1}", hash, jsonString);
        }
    }
}