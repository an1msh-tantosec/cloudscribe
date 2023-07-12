﻿using cloudscribe.Core.Models;
using cloudscribe.Core.Models.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Novell.Directory.Ldap;
using System;
using System.Collections.Generic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace cloudscribe.Core.Ldap
{
    //https://stackoverflow.com/questions/49682644/asp-net-core-2-0-ldap-active-directory-authentication

    public class LdapHelper : ILdapHelper
    {
        public LdapHelper(
            ILdapSslCertificateValidator ldapSslCertificateValidator,
            ILogger<LdapHelper> logger,
            IMemoryCache memoryCache
            )
        {
            _ldapSslCertificateValidator = ldapSslCertificateValidator;
            _log = logger;
            _memoryCache = memoryCache;
        }

        private readonly ILdapSslCertificateValidator _ldapSslCertificateValidator;
        private readonly ILogger _log;

        private readonly IMemoryCache _memoryCache;

        public bool IsImplemented { get; } = true;

        public Task<LdapUser> TryLdapLogin(
            ILdapSettings ldapSettings,
            string userName,
            string password,
            string siteId = null)
        {
            LdapUser user = null;

            var isValid = ValidateUser(ldapSettings, userName, password, siteId);

            if (isValid)
            {
                user = new LdapUser()
                {
                    CommonName = userName
                };
            }

            return Task.FromResult(user);
        }

        public Task<Dictionary<string,string>> TestLdapServers(
            ILdapSettings settings,
            string username,
            string password)
        {
            var result = new Dictionary<string,string>();
            var userDn = makeUserDN(settings, username);

            //determine which LDAP server to use
            string[] servers = settings.LdapServer.Split(',');
            int activeConnection = 0;
            while(activeConnection < servers.Length) //only try each server in the list once
            {
                string activeServer = servers[activeConnection].Trim();
                string message = $"Test Querying LDAP server: {activeServer} for {userDn} -";
                try
                {
                    using (var connection = GetConnection(activeServer, settings.LdapPort, settings.LdapUseSsl))
                    {
                        connection.Bind(userDn, password);
                        if (connection.Bound)
                        {
                            _log.LogInformation($"{message} bind succeeded");
                            LdapEntry entry = GetOneUserEntry(connection, settings, username);
                            string mail = entry.GetAttribute("mail").StringValue;
                            string givenName = entry.GetAttribute("givenName").StringValue;
                            string sn = entry.GetAttribute("sn").StringValue;
                            string displayName = entry.GetAttribute("displayName").StringValue;
                            _log.LogInformation($"{message} user details query succeeded.\nmail: {mail}\ngivenName: {givenName}\nsn: {sn}\ndisplayName: {displayName}");
                            result.Add(activeServer,"PASS");
                        }
                        else
                        {
                            _log.LogWarning($"{message} bind failed");
                            result.Add(activeServer,"Invalid Credentials");
                        }
                    }
                }
                catch(Exception ex)
                {
                    switch (ex.Message) {
                        case "Connect Error":
                            _log.LogError($"{message} connect to LDAP server failed");
                            result.Add(activeServer,ex.Message);
                            break;
                        case "Invalid Credentials":
                            _log.LogWarning($"{message} bind failed");
                            result.Add(activeServer,ex.Message);
                            break;
                        default:
                            _log.LogError($"{message} connect to LDAP server failed. The exception was:\n{ex.Message}:{ex.StackTrace}");
                            result.Add(activeServer, ex.Message);
                            break;
                    }
                }
                activeConnection++;
            }

            return Task.FromResult(result);
        }

        private string makeUserDN(ILdapSettings settings , string username)
        {
            //determine which DN format the LDAP server uses for users
            string userDn;
            switch (settings.LdapUserDNFormat)
            {
                case "username@LDAPDOMAIN":     //support for Active Directory
                    userDn = $"{username}@{settings.LdapDomain}";
                    break;
                case "uid=username,LDAPDOMAIN": //new additional support for Open LDAP or 389 Directory Server
                    userDn = $"uid={username},{settings.LdapDomain}";
                    break;
                default:
                    userDn = $"{settings.LdapDomain}\\{username}"; //is this also for AD ??
                    break;
            }
            return userDn;
        }

        private string makeUserFilter(ILdapSettings settings , string username)
        {
            //determine which format a user filter should be in
            string filter;
            switch (settings.LdapUserDNFormat)
            {
                case "username@LDAPDOMAIN":     //support for Active Directory
                    filter = $"(&(objectclass=person)(sAMAccountName={username}))";
                    break;
                case "uid=username,LDAPDOMAIN": //new additional support for Open LDAP or 389 Directory Server
                    filter = $"(&(|(objectclass=person)(objectclass=iNetOrgPerson))(uid={username}))";
                    break;
                default:
                    filter = $"(sAMAccountName={username})"; //is this also for AD ??
                    break;
            }
            return filter;
        }

        private bool ValidateUser(
            ILdapSettings settings,
            string username,
            string password,
            string siteId)
        {
            var userDn = makeUserDN(settings, username);

            //determine which LDAP server to use
            string[] servers = settings.LdapServer.Split(',');
            string memCacheKey = $"LdapActiveConnection_{siteId}"; //support for multi-tenancy
            int activeConnection = _memoryCache.TryGetValue<int>(memCacheKey, out activeConnection) ? activeConnection : 0;
            string activeServer = servers[activeConnection].Trim();

            int tryCount = 0;
            while (tryCount < servers.Length) //only try each server in the list once
            {
                string message = $"Querying LDAP server: {activeServer} for {userDn} -";
                try
                {
                    using (var connection = GetConnection(activeServer, settings.LdapPort, settings.LdapUseSsl))
                    {
                        connection.Bind(userDn, password);
                        if (connection.Bound)
                        {
                            _log.LogInformation($"{message} bind succeeded");
                            _memoryCache.Set(memCacheKey, activeConnection);
                            return true;
                        }
                        else
                        {
                            _log.LogWarning($"{message} bind failed");
                            _memoryCache.Set(memCacheKey, activeConnection);
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if(ex.Message == "Invalid Credentials") {
                        _log.LogWarning($"{message} bind failed");
                        _memoryCache.Set(memCacheKey, activeConnection);
                        return false;
                    }
                    if(ex.Message == "Connect Error") {
                        _log.LogError($"{message} connect to LDAP server failed");
                    }
                    else
                    {
                        _log.LogError($"{message} {ex.Message}:\n{ex.StackTrace}");
                    }
                }

                activeConnection = (activeConnection + 1) % servers.Length;
                activeServer = servers[activeConnection].Trim();
                tryCount++;
            }
            _log.LogError($"All LDAP servers failed for {userDn}");
            return false;
        }

        private LdapConnection GetConnection(string server, int port, bool useSsl = false)
        {
            LdapConnection conn = new LdapConnection();

            if (useSsl)
            {
                // make this support ssl/tls
                //http://stackoverflow.com/questions/386982/novell-ldap-c-novell-directory-ldap-has-anybody-made-it-work
                conn.SecureSocketLayer = true;
                conn.UserDefinedServerCertValidationDelegate += LdapSSLCertificateValidator;
            }

            conn.Connect(server, port);
            return conn;
        }


        private bool LdapSSLCertificateValidator(
           object sender,
           X509Certificate certificate,
           X509Chain chain,
           SslPolicyErrors sslPolicyErrors)
        {
            return _ldapSslCertificateValidator.ValidateCertificate(
                sender,
                certificate,
                chain,
                sslPolicyErrors
                );

        }

        private LdapEntry GetOneUserEntry(
            LdapConnection conn,
            ILdapSettings ldapSettings,
            string username)
        {
            string baseDn = ldapSettings.LdapDomain;
            string filter = makeUserFilter(ldapSettings, username);
            LdapEntry entry = null;
            LdapSearchConstraints cons = new LdapSearchConstraints();
            cons.TimeLimit = 10000 ;
            string[] attrs = new string[] { "cn", "mail", "givenName", "sn", "displayName" };

            var lsc = conn.Search(
                baseDn,
                LdapConnection.ScopeSub,
                filter,
                null,
                false,
                cons
                );

            while (lsc.HasMore())
            {
                LdapEntry nextEntry = null;
                try
                {
                    nextEntry = lsc.Next();
                    return nextEntry;
                }
                catch(LdapException e)
                {
                    Console.WriteLine("Error: " + e.LdapErrorMessage);
                    //Exception is thrown, go for next entry
                    continue;
                }

                Console.WriteLine("\n" + nextEntry.Dn);

                // Get the attribute set of the entry
                LdapAttributeSet attributeSet = nextEntry.GetAttributeSet();
                System.Collections.IEnumerator ienum = attributeSet.GetEnumerator();

                // Parse through the attribute set to get the attributes and the  corresponding values
                while(ienum.MoveNext())
                {
                    LdapAttribute attribute=(LdapAttribute)ienum.Current;
                    string attributeName = attribute.Name;
                    string attributeVal = attribute.StringValue;
                    Console.WriteLine( attributeName + "value:" + attributeVal);
                }
            }


        //     queue = conn.Search(
        //         ldapSettings.LdapRootDN,
        //         LdapConnection.SCOPE_SUB,
        //         filter,
        //         null,
        //         false,
        //         (LdapSearchQueue)null,
        //         (LdapSearchConstraints)null
        //         );

        //    if (queue != null)
        //    {
        //        LdapMessage message = queue.getResponse();
        //        if (message != null)
        //        {
        //            if (message is LdapSearchResult)
        //            {
        //                entry = ((LdapSearchResult)message).Entry;
        //            }
        //        }
        //    }
        //    else
        //    {
        //        _log.LogWarning("queue was null");
        //    }
           return entry;
        }

        //private LdapUser BuildUserFromEntry(LdapEntry entry)
        //{
        //    var user = new LdapUser();

        //    LdapAttributeSet las = entry.getAttributeSet();

        //    foreach (LdapAttribute a in las)
        //    {
        //        switch (a.Name)
        //        {
        //            case "mail":
        //                user.Email = a.StringValue;
        //                break;
        //            case "cn":
        //                user.CommonName = a.StringValue;
        //                break;
        //            case "userPassword":
        //                // this.password = a;
        //                break;
        //            case "uidNumber":
        //                //this.uidNumber = a;
        //                break;
        //            case "uid":
        //                // this.userid = a;
        //                break;
        //            case "sAMAccountName":
        //                // this.userid = a;
        //                break;
        //            case "givenName":
        //                user.FirstName = a.StringValue;
        //                break;
        //            case "sn":
        //                user.LastName = a.StringValue;
        //                break;
        //        }
        //    }

        //    return user;
        //}


        //private LdapUser LdapStandardLogin(ILdapSettings ldapSettings, string userName, string password, bool useSsl)
        //{
        //    bool success = false;
        //    LdapUser user = null;

        //    LdapConnection conn = null;
        //    try
        //    {
        //        using (conn = GetConnection(ldapSettings, useSsl))
        //        {

        //            if ((conn != null) && (conn.Connected))
        //            {
        //                LdapEntry entry = null;

        //                try
        //                {
        //                    entry = GetOneUserEntry(conn, ldapSettings, userName);
        //                    if (entry != null)
        //                    {
        //                        conn.Bind(entry.DN, password);
        //                        success = true;
        //                    }
        //                    else
        //                    {
        //                        _log.LogWarning($"could not find entry for {userName}");
        //                    }
        //                }
        //                catch (Exception ex)
        //                {
        //                    string msg = $"Login failure for user: {userName} Exception: {ex.Message}:{ex.StackTrace}";
        //                    _log.LogError(msg);

        //                    success = false;
        //                }

        //                if (success)
        //                {
        //                    if (entry != null)
        //                    {
        //                        user = BuildUserFromEntry(entry);
        //                    }
        //                }

        //                conn.Disconnect();
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        string msg = $"Login failure for user: {userName} Exception: {ex.Message}:{ex.StackTrace}";
        //        _log.LogError(msg);

        //    }

        //    return user;
        //}

        //private LdapEntry GetOneUserEntry(
        //    LdapConnection conn,
        //    ILdapSettings ldapSettings,
        //    string userName)
        //{

        //    LdapSearchConstraints constraints = new LdapSearchConstraints();

        //    var filter = "(&(sAMAccountName=" + userName + "))";
        //    //ldapSettings.LdapUserDNKey + "=" + search,

        //    LdapSearchQueue queue = null;
        //    queue = conn.Search(
        //        ldapSettings.LdapRootDN,
        //        LdapConnection.SCOPE_SUB,
        //        filter,
        //        null,
        //        false,
        //        (LdapSearchQueue)null,
        //        (LdapSearchConstraints)null
        //        );



        //    LdapEntry entry = null;

        //    if (queue != null)
        //    {
        //        LdapMessage message = queue.getResponse();
        //        if (message != null)
        //        {
        //            if (message is LdapSearchResult)
        //            {
        //                entry = ((LdapSearchResult)message).Entry;
        //            }
        //        }
        //    }
        //    else
        //    {
        //        _log.LogWarning("queue was null");
        //    }

        //    return entry;
        //}



    }
}
