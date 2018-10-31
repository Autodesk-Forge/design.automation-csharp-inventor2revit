/////////////////////////////////////////////////////////////////////
// Copyright (c) Autodesk, Inc. All rights reserved
// Written by Forge Partner Development
//
// Permission to use, copy, modify, and distribute this software in
// object code form for any purpose and without fee is hereby granted,
// provided that the above copyright notice appears in all copies and
// that both that copyright notice and the limited warranty and
// restricted rights notice below appear in all supporting
// documentation.
//
// AUTODESK PROVIDES THIS PROGRAM "AS IS" AND WITH ALL FAULTS.
// AUTODESK SPECIFICALLY DISCLAIMS ANY IMPLIED WARRANTY OF
// MERCHANTABILITY OR FITNESS FOR A PARTICULAR USE.  AUTODESK, INC.
// DOES NOT WARRANT THAT THE OPERATION OF THE PROGRAM WILL BE
// UNINTERRUPTED OR ERROR FREE.
/////////////////////////////////////////////////////////////////////

using Amazon.S3;
using Autodesk.Forge;
using Autodesk.Forge.DesignAutomation.v3;
using Autodesk.Forge.Model;
using Autodesk.Forge.Model.DesignAutomation.v3;
using Newtonsoft.Json.Linq;
using RestSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using ActivitiesApi = Autodesk.Forge.DesignAutomation.v3.ActivitiesApi;
using Activity = Autodesk.Forge.Model.DesignAutomation.v3.Activity;
using WorkItem = Autodesk.Forge.Model.DesignAutomation.v3.WorkItem;
using WorkItemsApi = Autodesk.Forge.DesignAutomation.v3.WorkItemsApi;

namespace Inventor2Revit.Controllers
{
    public class DesignAutomation4Inventor
    {
        private const string APPNAME = "IptToSatApp";
        private const string APPBUNBLENAME = "IptToSatAppBundle.zip";
        private const string ACTIVITY_NAME = "IptToSatActivity";
        private const string ALIAS = "v1";

        public static string NickName
        {
            get
            {
                return Credentials.GetAppSetting("FORGE_CLIENT_ID");
            }
        }

        public async Task EnsureAppBundle(string appAccessToken, string contentRootPath)
        {
            //List<string> apps = await da.GetAppBundles(nickName);
            AppBundlesApi appBundlesApi = new AppBundlesApi();
            appBundlesApi.Configuration.AccessToken = appAccessToken;

            // at this point we can either call get by alias/id and catch or get a list and check
            //dynamic appBundle = await appBundlesApi.AppbundlesByIdAliasesByAliasIdGetAsync(APPNAME, ALIAS);

            // or get the list and check for the name
            PageString appBundles = await appBundlesApi.AppBundlesGetItemsAsync();
            bool existAppBundle = false;
            foreach (string appName in appBundles.Data)
            {
                if (appName.Contains(string.Format("{0}.{1}+{2}", NickName, APPNAME, ALIAS)))
                {
                    existAppBundle = true;
                    continue;
                }
            }

            if (!existAppBundle)
            {
                // check if ZIP with bundle is here
                string packageZipPath = Path.Combine(contentRootPath, APPBUNBLENAME);
                if (!System.IO.File.Exists(packageZipPath)) throw new Exception("IptToSat appbundle not found at " + packageZipPath);

                // create bundle
                AppBundle appBundleSpec = new AppBundle(APPNAME, null, "Autodesk.Inventor+23", null, null, APPNAME, null, APPNAME);
                AppBundle newApp = await appBundlesApi.AppBundlesCreateItemAsync(appBundleSpec);
                if (newApp == null) throw new Exception("Cannot create new app");

                // create alias
                Alias aliasSpec = new Alias(1, null, ALIAS);
                Alias newAlias = await appBundlesApi.AppBundlesCreateAliasAsync(APPNAME, aliasSpec);

                // upload the zip with .bundle
                RestClient uploadClient = new RestClient(newApp.UploadParameters.EndpointURL);
                RestRequest request = new RestRequest(string.Empty, Method.POST);
                request.AlwaysMultipartFormData = true;
                foreach (KeyValuePair<string, object> x in newApp.UploadParameters.FormData)
                    request.AddParameter(x.Key, x.Value);
                request.AddFile("file", packageZipPath);
                request.AddHeader("Cache-Control", "no-cache");
                var res = await uploadClient.ExecuteTaskAsync(request);
            }
        }

        public async Task EnsureActivity(string appAccessToken)
        {
            ActivitiesApi activitiesApi = new ActivitiesApi();
            activitiesApi.Configuration.AccessToken = appAccessToken;
            PageString activities = await activitiesApi.ActivitiesGetItemsAsync();

            bool existActivity = false;
            foreach (string activity in activities.Data)
            {
                if (activity.Contains(string.Format("{0}.{1}+{2}", NickName, ACTIVITY_NAME, ALIAS)))
                {
                    existActivity = true;
                    continue;
                }
            }

            if (!existActivity)
            {
                // create activity
                string commandLine = string.Format(@"$(engine.path)\\InventorCoreConsole.exe /i $(args[InventorDoc].path) /al $(appbundles[{0}].path)", APPNAME);
                ModelParameter iptFile = new ModelParameter(false, false, ModelParameter.VerbEnum.Get, "Input IPT File", true, "$(InventorDoc)");
                ModelParameter resultSat = new ModelParameter(false, false, ModelParameter.VerbEnum.Put, "Resulting SAT File", true, "export.sat");
                Activity activitySpec = new Activity(
                  new List<string>() { commandLine },
                  new Dictionary<string, ModelParameter>() {
                    { "InventorDoc", iptFile },
                    { "export", resultSat }
                  },
                  "Autodesk.Inventor+23",
                  new List<string>() { string.Format("{0}.{1}+{2}", NickName, APPNAME, ALIAS) },
                  null,
                  ACTIVITY_NAME,
                  null,
                  ACTIVITY_NAME);
                Activity newActivity = await activitiesApi.ActivitiesCreateItemAsync(activitySpec);

                // create alias
                Alias aliasSpec = new Alias(1, null, ALIAS);
                Alias newAlias = await activitiesApi.ActivitiesCreateAliasAsync(ACTIVITY_NAME, aliasSpec);
            }
        }

        private async Task<JObject> BuildDownloadURL(string userAccessToken, string projectId, string versionId)
        {
            VersionsApi versionApi = new VersionsApi();
            versionApi.Configuration.AccessToken = userAccessToken;
            dynamic version = await versionApi.GetVersionAsync(projectId, versionId);
            dynamic versionItem = await versionApi.GetVersionItemAsync(projectId, versionId);

            string[] versionItemParams = ((string)version.data.relationships.storage.data.id).Split('/');
            string[] bucketKeyParams = versionItemParams[versionItemParams.Length - 2].Split(':');
            string bucketKey = bucketKeyParams[bucketKeyParams.Length - 1];
            string objectName = versionItemParams[versionItemParams.Length - 1];
            string downloadUrl = string.Format("https://developer.api.autodesk.com/oss/v2/buckets/{0}/objects/{1}", bucketKey, objectName);

            return new JObject
            {
                new JProperty("url", downloadUrl),
                new JProperty("headers",
                new JObject{
                    new JProperty("Authorization", "Bearer " + userAccessToken)
                })
            };
        }

        private async Task<JObject> BuildUploadURL(string resultFilename)
        {
            IAmazonS3 client = new AmazonS3Client(Amazon.RegionEndpoint.USWest2);

            if (!await client.DoesS3BucketExistAsync(Utils.S3BucketName))
                await client.EnsureBucketExistsAsync(Utils.S3BucketName);

            Dictionary<string, object> props = new Dictionary<string, object>();
            props.Add("Verb", "PUT");
            Uri uploadToS3 = new Uri(client.GeneratePreSignedURL(Utils.S3BucketName, resultFilename, DateTime.Now.AddMinutes(10), props));

            return new JObject
            {
                new JProperty("verb", "PUT"),
                new JProperty("url", uploadToS3.ToString())
            };
        }

        public async Task StartInventorIPT2SAT(string userId, string projectId, string versionId, string contentRootPath)
        {
            TwoLeggedApi oauth = new TwoLeggedApi();
            string appAccessToken = (await oauth.AuthenticateAsync(Credentials.GetAppSetting("FORGE_CLIENT_ID"), Credentials.GetAppSetting("FORGE_CLIENT_SECRET"), oAuthConstants.CLIENT_CREDENTIALS, new Scope[] { Scope.CodeAll })).ToObject<Bearer>().AccessToken;

            // uncomment these lines to clear all appbundles & activities under your account
            Autodesk.Forge.DesignAutomation.v3.ForgeAppsApi forgeAppApi = new ForgeAppsApi();
            forgeAppApi.Configuration.AccessToken = appAccessToken;
            await forgeAppApi.ForgeAppsDeleteUserAsync("me");

            Credentials credentials = await Credentials.FromDatabaseAsync(userId);

            await EnsureAppBundle(appAccessToken, contentRootPath);
            await EnsureActivity(appAccessToken);

            string resultFilename = versionId.Base64Encode() + ".sat";
            string callbackUrl = string.Format("{0}/api/forge/callback/designautomation/inventor/{1}/{2}/{3}", Credentials.GetAppSetting("FORGE_WEBHOOK_CALLBACK_HOST"), userId, projectId, versionId.Base64Encode());
            WorkItem workItemSpec = new WorkItem(
              null,
              string.Format("{0}.{1}+{2}", NickName, ACTIVITY_NAME, ALIAS),
              new Dictionary<string, JObject>()
              {
                  { "InventorDoc", await BuildDownloadURL(credentials.TokenInternal, projectId, versionId) },
                  { "export", await BuildUploadURL(resultFilename)  },
                  { "onComplete", new JObject { new JProperty("verb", "POST"), new JProperty("url", callbackUrl) }}
              },
              null);
            WorkItemsApi workItemApi = new WorkItemsApi();
            workItemApi.Configuration.AccessToken = appAccessToken;
            WorkItemStatus newWorkItem = await workItemApi.WorkItemsCreateWorkItemsAsync(null, null, workItemSpec);
        }
    }
}