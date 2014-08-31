﻿using JFrog.Artifactory.Model;
using JFrog.Artifactory.Utils.httpClient;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace JFrog.Artifactory.Utils
{
    /// <summary>
    /// Artifactory client to perform build info related tasks.
    /// <summary>
    class ArtifactoryBuildInfoClient
    {
        private static String BUILD_REST_URL = "/api/build";
        private static String BUILD_BROWSE_URL = "/webapp/builds";

        /* Try checksum deploy of files greater than 10KB */
        private static readonly int CHECKSUM_DEPLOY_MIN_FILE_SIZE = 10240; 
        private ArtifactoryHttpClient _httpClient;
        private string _artifactoryUrl;
        private TaskLoggingHelper _log;

        //public ArtifactoryBuildInfoClient(string artifactoryUrl) {
        //    (artifactoryUrl, null, null);
        //}

        public ArtifactoryBuildInfoClient(string artifactoryUrl, string username, string password, TaskLoggingHelper log)
        {
            _artifactoryUrl = artifactoryUrl;
            _httpClient = new ArtifactoryHttpClient(artifactoryUrl, username, password);
            _artifactoryUrl = artifactoryUrl; 
            _log = log;
        }

        public void sendBuildInfo(Build buildInfo) {
            try
            {               
                sendBuildInfo(buildInfo.ToJsonString());
                _log.LogMessageFromText("Build successfully deployed. Browse it in Artifactory under " + string.Format(_artifactoryUrl + BUILD_BROWSE_URL) +
                    "/" + buildInfo.name + "/" + buildInfo.number + "/" + buildInfo.started + "/", MessageImportance.High);
            }
            catch (Exception ex)
            {
                _log.LogMessageFromText("Could not publish the build-info object: " + ex.InnerException, MessageImportance.High);
                throw new Exception("Could not publish build-info", ex);
            }            
        }

        public void sendBuildInfo(String buildInfoJson)
        {
            string url = _artifactoryUrl + BUILD_REST_URL;
            
            _log.LogMessageFromText("Uploading build info to Artifactory...", MessageImportance.High);

            try
            {
                var bytes = Encoding.Default.GetBytes(buildInfoJson);
                {
                    //Custom headers
                    WebHeaderCollection headers = new WebHeaderCollection();
                    headers.Add(HttpRequestHeader.ContentType, "application/vnd.org.jfrog.build.BuildInfo+json");
                    _httpClient.getHttpClient().setHeader(headers);
                        
                    HttpResponse response = _httpClient.getHttpClient().execute(url, "PUT", bytes);
                    
                    ///When sending build info, Expecting for NoContent (204) response from Artifactory 
                    if (response._statusCode != HttpStatusCode.NoContent) 
                    {
                        throw new WebException("Failed to send build info:" + response._message);  
                    }                   
                }
            }
            catch (Exception we) {
                _log.LogMessageFromText(we.Message, MessageImportance.High);
                throw new WebException("Exception in Uploading BuildInfo: " + we.Message, we);
            }
        }

        public void deployArtifact(DeployDetails details) 
        {
            if (tryChecksumDeploy(details, _artifactoryUrl))
            {
                return;
            }

            //Custom headers
            WebHeaderCollection headers = new WebHeaderCollection();
            headers = createHttpPutMethod(details);
            headers.Add(HttpRequestHeader.ContentType, "binary/octet-stream");

            /*
             * "100 (Continue)" status is to allow a client that is sending a request message with a request body to determine if the origin server is
             *  willing to accept the request (based on the request headers) before the client sends the request body.
             */
            //headers.Add("Expect", "100-continue");

            _httpClient.getHttpClient().setHeader(headers);

            byte[] data = File.ReadAllBytes(details.file.FullName);

            string deploymentPath = _artifactoryUrl + "/" + details.targetRepository + "/" + details.artifactPath;

            /* Add properties to the artifact, if any */
            deploymentPath = deploymentPath + details.properties;

            _log.LogMessageFromText("Deploying artifact: " + deploymentPath, MessageImportance.High);
            HttpResponse response = _httpClient.getHttpClient().execute(deploymentPath, "PUT", data);

            ///When deploying artifact, Expecting for Created (201) response from Artifactory 
            if ((response._statusCode != HttpStatusCode.OK) && (response._statusCode != HttpStatusCode.Created))
            {
                _log.LogMessageFromText("Error occurred while publishing artifact to Artifactory: " + details.file, MessageImportance.High);
                throw new WebException("Failed to deploy file:" + response._message);
            }    
        }

        /// <summary>
        ///  Deploy an artifact to the specified destination by checking if the artifact content already exists in Artifactory
        /// </summary>
        private Boolean tryChecksumDeploy(DeployDetails details, String uploadUrl) 
        {
            // Try checksum deploy only on file size greater than CHECKSUM_DEPLOY_MIN_FILE_SIZE
            if (details.file.Length < CHECKSUM_DEPLOY_MIN_FILE_SIZE) {
                _log.LogMessageFromText("Skipping checksum deploy of file size " + details.file.Length + " , falling back to regular deployment.",
                                            MessageImportance.High);
                return false;
            }

            string checksumUrlPath = uploadUrl + "/" + details.targetRepository + "/" + details.artifactPath;

            /* Add properties to the artifact, if any */
            checksumUrlPath = checksumUrlPath + details.properties;

            WebHeaderCollection headers = createHttpPutMethod(details);
            headers.Add("X-Checksum-Deploy", "true");
            headers.Add(HttpRequestHeader.ContentType, "application/vnd.org.jfrog.artifactory.storage.ItemCreated+json");

            _httpClient.getHttpClient().setHeader(headers);
            HttpResponse response = _httpClient.getHttpClient().execute(checksumUrlPath, "PUT");

            ///When sending Checksum deploy, Expecting for Created (201) or Success (200) responses from Artifactory 
            if (response._statusCode == HttpStatusCode.Created || response._statusCode == HttpStatusCode.OK)
            {

                _log.LogMessageFromText(string.Format("Successfully performed checksum deploy of file {0} : {1}", details.file.FullName, details.sha1)
                                                , MessageImportance.High);
                return true;
            }
            else 
            {
                _log.LogMessageFromText(string.Format("Failed checksum deploy of checksum '{0}' with statusCode: {1}", details.sha1, response._statusCode)
                                                , MessageImportance.High);
            }

            return false;
        }

        /// <summary>
        /// Typical PUT header with Checksums, for deploying files to Artifactory 
        /// </summary>
        private WebHeaderCollection createHttpPutMethod(DeployDetails details)
        {
            WebHeaderCollection putHeaders = new WebHeaderCollection();
            putHeaders.Add("X-Checksum-Sha1", details.sha1);
            putHeaders.Add("X-Checksum-Md5", details.md5);

            return putHeaders;
        }

        public void Dispose()
        {
            if (_httpClient != null)
            {
                _httpClient.Dispose();
            }
        }
    }
}
