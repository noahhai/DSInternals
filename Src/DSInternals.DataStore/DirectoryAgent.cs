﻿namespace DSInternals.DataStore
{
    using DSInternals.Common;
    using DSInternals.Common.Cryptography;
    using DSInternals.Common.Data;
    using DSInternals.Common.Exceptions;
    using DSInternals.Common.Properties;
    using Microsoft.Database.Isam;
    using System;
    using System.Collections.Generic;
    using System.Security.Principal;
    public class DirectoryAgent : IDisposable
    {
        // 2^30
        public const int RidMax = 1 << 30;

        //TODO: SidCompatibilityVersion?
        // TODO: Add Rid range checks
        public const int RidMin = 1;

        private DirectoryContext context;
        private Cursor dataTableCursor;
        private bool ownsContext;

        public DirectoryAgent(DirectoryContext context, bool ownsContext = false)
        {
            this.context = context;
            this.ownsContext = ownsContext;
            this.dataTableCursor = context.OpenDataTable();
        }

        public void SetDomainControllerEpoch(int epoch)
        {
            using (var transaction = this.context.BeginTransaction())
            {
                this.context.DomainController.Epoch = epoch;
                transaction.Commit(true);
            }
        }

        public void SetDomainControllerUsn(long highestCommittedUsn)
        {
            using (var transaction = this.context.BeginTransaction())
            {
                this.context.DomainController.HighestCommittedUsn = highestCommittedUsn;
                transaction.Commit(true);
            }
        }

        public void ChangeBootKey(byte[] oldBootKey, byte[] newBootKey)
        {
            // Validate
            Validator.AssertLength(oldBootKey, BootKeyRetriever.BootKeyLength, "oldBootKey");
            if (!this.context.DomainController.DomainNamingContextDNT.HasValue)
            {
                // The domain object must exist
                throw new DirectoryObjectNotFoundException("domain");
            }
            
            // Execute
            using (var transaction = this.context.BeginTransaction())
            {
                // Retrieve and decrypt
                var domain = this.FindObject(this.context.DomainController.DomainNamingContextDNT.Value);
                byte[] encryptedPEK;
                domain.ReadAttribute(CommonDirectoryAttributes.PEKList, out encryptedPEK);
                var pekList = (DataStoreSecretDecryptor) new DataStoreSecretDecryptor(encryptedPEK, oldBootKey);
                
                // Encrypt with the new boot key (if blank, plain encoding is done instead)
                byte[] binaryPekList = pekList.ToByteArray(newBootKey);
                
                // Save the new value
                this.dataTableCursor.BeginEditForUpdate();
                bool hasChanged = domain.SetAttribute(CommonDirectoryAttributes.PEKList, binaryPekList);
                this.CommitAttributeUpdate(domain, CommonDirectoryAttributes.PEKList, transaction, hasChanged, true);
            }
        }

        public IEnumerable<DSAccount> GetAccounts(byte[] bootKey)
        {
            var pek = this.GetSecretDecryptor(bootKey);
            // TODO: Use a more suitable index?
            string samAccountTypeIndex = this.context.Schema.FindIndexName(CommonDirectoryAttributes.SamAccountType);
            this.dataTableCursor.CurrentIndex = samAccountTypeIndex;
            // Find all objects with the right sAMAccountType that are writable and not deleted:
            // TODO: Lock cursor?
            while (this.dataTableCursor.MoveNext())
            {
                var obj = new DatastoreObject(this.dataTableCursor, this.context);
                // TODO: This probably does not work on RODCs:
                if(obj.IsDeleted || !obj.IsWritable || !obj.IsAccount)
                {
                    continue;
                }
                yield return new DSAccount(obj, pek);
            }
        }

        public DSAccount GetAccount(DistinguishedName dn, byte[] bootKey)
        {
            var obj = this.FindObject(dn);
            return this.GetAccount(obj, dn, bootKey);
        }

        public DSAccount GetAccount(SecurityIdentifier objectSid, byte[] bootKey)
        {
            var obj = this.FindObject(objectSid);
            return this.GetAccount(obj, objectSid, bootKey);
        }

        public DSAccount GetAccount(string samAccountName, byte[] bootKey)
        {
            var obj = this.FindObject(samAccountName);
            return this.GetAccount(obj, samAccountName, bootKey);
        }

        public DSAccount GetAccount(Guid objectGuid, byte[] bootKey)
        {
            var obj = this.FindObject(objectGuid);
            return this.GetAccount(obj, objectGuid, bootKey);
        }

        protected DSAccount GetAccount(DatastoreObject foundObject, object objectIdentifier, byte[] bootKey)
        {
            if (!foundObject.IsAccount)
            {
                throw new DirectoryObjectOperationException(Resources.ObjectNotSecurityPrincipalMessage, objectIdentifier);
            }

            var pek = GetSecretDecryptor(bootKey);
            return new DSAccount(foundObject, pek);
        }

        protected DirectorySecretDecryptor GetSecretDecryptor(byte[] bootKey)
        {
            if (bootKey == null && ! this.context.DomainController.IsADAM)
            {
                // This is an AD DS DB, so the BootKey is stored in the registry. Stop processing if it is not provided.
                return null;

            }
            if(this.context.DomainController.State == DatabaseState.Boot)
            {
                // The initial DB definitely does not contain any secrets.
                return null;
            }
           
            // HACK: Save the current cursor position, because it is shared.
            var originalLocation = this.dataTableCursor.SaveLocation();
            try
            {
                int pekListDNT;
                if(this.context.DomainController.IsADAM)
                {
                    // This is a AD LDS DB, so the BootKey is stored directly in the DB.
                    // Retrieve the pekList attribute of the root object:
                    byte[] rootPekList;
                    var rootObject = this.FindObject(ADConstants.RootDNTag);
                    rootObject.ReadAttribute(CommonDirectoryAttributes.PEKList, out rootPekList);

                    // Retrieve the pekList attribute of the schema object:
                    byte[] schemaPekList;
                    var schemaObject = this.FindObject(this.context.DomainController.SchemaNamingContextDNT);
                    schemaObject.ReadAttribute(CommonDirectoryAttributes.PEKList, out schemaPekList);

                    // Combine these things together into the BootKey/SysKey
                    bootKey = BootKeyRetriever.GetBootKey(rootPekList, schemaPekList);

                    // The actual PEK list is located on the Configuration NC object.
                    pekListDNT = this.context.DomainController.ConfigurationNamingContextDNT;
                }
                else
                {
                    // This is an AD DS DB, so the PEK list is located on the Domain NC object.
                    pekListDNT = this.context.DomainController.DomainNamingContextDNT.Value;
                }

                // Load the PEK List attribute from the holding object and decrypt it using Boot Key.
                var pekListHolder = this.FindObject(pekListDNT);
                byte[] encryptedPEK;
                pekListHolder.ReadAttribute(CommonDirectoryAttributes.PEKList, out encryptedPEK);
                return new DataStoreSecretDecryptor(encryptedPEK, bootKey);
            }
            finally
            {
                this.dataTableCursor.RestoreLocation(originalLocation);
            }
        }
        
        public IEnumerable<DPAPIBackupKey> GetDPAPIBackupKeys(byte[] bootKey)
        {
            Validator.AssertNotNull(bootKey, "bootKey");
            var pek = this.GetSecretDecryptor(bootKey);
            // TODO: Refactor using Linq
            foreach(var secret in this.FindObjectsByCategory(CommonDirectoryClasses.Secret))
            {
                yield return new DPAPIBackupKey(secret, pek);
            }
        }

        public IEnumerable<KdsRootKey> GetKdsRootKeys()
        {
            // TODO: Refactor using Linq
            // TODO: Test if schema contains the ms-Kds-Prov-RootKey class.
            foreach (var keyObject in this.FindObjectsByCategory(CommonDirectoryClasses.KdsRootKey))
            {
                yield return new KdsRootKey(keyObject);
            }
        }

        public IEnumerable<DirectoryObject> FindObjectsByCategory(string className, bool includeDeleted = false)
        {
            // Find all objects with the right objectCategory
            string objectCategoryIndex = this.context.Schema.FindIndexName(CommonDirectoryAttributes.ObjectCategory);
            this.dataTableCursor.CurrentIndex = objectCategoryIndex;
            int classId = this.context.Schema.FindClassId(className);
            this.dataTableCursor.FindRecords(MatchCriteria.EqualTo, Key.Compose(classId));
            // TODO: Lock cursor?
            while (this.dataTableCursor.MoveNext())
            {
                var obj = new DatastoreObject(this.dataTableCursor, this.context);
                // Optionally skip deleted objects
                if (!includeDeleted && obj.IsDeleted)
                {
                    continue;
                }
                yield return obj;
            }
        }

        public bool AddSidHistory(DistinguishedName dn, SecurityIdentifier[] sidHistory, bool skipMetaUpdate)
        {
            var obj = this.FindObject(dn);
            return this.AddSidHistory(obj, dn, sidHistory, skipMetaUpdate);
        }

        public bool AddSidHistory(string samAccountName, SecurityIdentifier[] sidHistory, bool skipMetaUpdate)
        {
            var obj = this.FindObject(samAccountName);
            return this.AddSidHistory(obj, samAccountName, sidHistory, skipMetaUpdate);
        }

        public bool AddSidHistory(SecurityIdentifier objectSid, SecurityIdentifier[] sidHistory, bool skipMetaUpdate)
        {
            var obj = this.FindObject(objectSid);
            return this.AddSidHistory(obj, objectSid, sidHistory, skipMetaUpdate);
        }

        public bool AddSidHistory(Guid objectGuid, SecurityIdentifier[] sidHistory, bool skipMetaUpdate)
        {
            var obj = this.FindObject(objectGuid);
            return this.AddSidHistory(obj, objectGuid, sidHistory, skipMetaUpdate);
        }

        public void AuthoritativeRestore(Guid objectGuid, string[] attributeNames)
        {
            // TODO: Implement attribute-level authorirative restore.
            // TODO: Check attribute names
            // TODO: Check attribute types (not linked and not system?)
            var obj = this.FindObject(objectGuid);
            throw new NotImplementedException();
        }

        public void AuthoritativeRestore(DistinguishedName dn, string[] attributeNames)
        {
            // TODO: Check attribute names
            // TODO: Check attribute types (not linked and not system?)
            this.FindObject(dn);
            throw new NotImplementedException();
        }
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="samAccountName"></param>
        /// <exception cref="DirectoryObjectNotFoundException"></exception>
        public DatastoreObject FindObject(string samAccountName)
        {
            string samAccountNameIndex = this.context.Schema.FindIndexName(CommonDirectoryAttributes.SAMAccountName);
            this.dataTableCursor.CurrentIndex = samAccountNameIndex;
            this.dataTableCursor.FindRecords(MatchCriteria.EqualTo, Key.Compose(samAccountName));

            // Find first object with the right sAMAccountName, that is writable and not deleted:
            while (this.dataTableCursor.MoveNext())
            {
                var currentObject = new DatastoreObject(this.dataTableCursor, this.context);
                if (currentObject.IsWritable && !currentObject.IsDeleted)
                {
                    return currentObject;
                }
            }
            // If the code execution comes here, we have not found any object matching the criteria.
            throw new DirectoryObjectNotFoundException(samAccountName);
        }

        /// <exception cref="DirectoryObjectNotFoundException"></exception>
        public DatastoreObject FindObject(SecurityIdentifier objectSid)
        {
            string sidIndex = this.context.Schema.FindIndexName(CommonDirectoryAttributes.ObjectSid);
            this.dataTableCursor.CurrentIndex = sidIndex;
            byte[] binarySid = objectSid.GetBinaryForm(true);
            bool found = this.dataTableCursor.GotoKey(Key.Compose(binarySid));
            if (found)
            {
                return new DatastoreObject(this.dataTableCursor, this.context);
            }
            else
            {
                throw new DirectoryObjectNotFoundException(objectSid);
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="dn"></param>
        /// <exception cref="DirectoryObjectNotFoundException"></exception>
        public DatastoreObject FindObject(DistinguishedName dn)
        {
            // This throws exception if the DN does not get resolved to dnt:
            int dnTag = this.context.DistinguishedNameResolver.Resolve(dn);
            return this.FindObject(dnTag);
        }

        public DatastoreObject FindObject(int dnTag)
        {
            string dntIndex = this.context.Schema.FindIndexName(CommonDirectoryAttributes.DNTag);
            this.dataTableCursor.CurrentIndex = dntIndex;
            bool found = this.dataTableCursor.GotoKey(Key.Compose(dnTag));
            if (found)
            {
                return new DatastoreObject(this.dataTableCursor, this.context);
            }
            else
            {
                throw new DirectoryObjectNotFoundException(dnTag);
            }
        }


        /// <exception cref="DirectoryObjectNotFoundException"></exception>
        public DatastoreObject FindObject(Guid objectGuid)
        {
            string objectGuidIndex = this.context.Schema.FindIndexName(CommonDirectoryAttributes.ObjectGUID);
            this.dataTableCursor.CurrentIndex = objectGuidIndex;
            bool found = this.dataTableCursor.GotoKey(Key.Compose(objectGuid.ToByteArray()));
            if (found)
            {
                return new DatastoreObject(this.dataTableCursor, this.context);
            }
            else
            {
                throw new DirectoryObjectNotFoundException(objectGuid);
            }
        }

        public void RemoveObject(Guid objectGuid)
        {
            var obj = this.FindObject(objectGuid);
            obj.Delete();
        }

        public void RemoveObject(DistinguishedName dn)
        {
            var obj = this.FindObject(dn);
            obj.Delete();
        }

        public bool SetAccountStatus(DistinguishedName dn, bool enabled, bool skipMetaUpdate)
        {
            var obj = this.FindObject(dn);
            return this.SetAccountStatus(obj, dn, enabled, skipMetaUpdate);
        }

        public bool SetAccountStatus(string samAccountName, bool enabled, bool skipMetaUpdate)
        {
            var obj = this.FindObject(samAccountName);
            return this.SetAccountStatus(obj, samAccountName, enabled, skipMetaUpdate);
        }

        public bool SetAccountStatus(SecurityIdentifier objectSid, bool enabled, bool skipMetaUpdate)
        {
            var obj = this.FindObject(objectSid);
            return this.SetAccountStatus(obj, objectSid, enabled, skipMetaUpdate);
        }

        public bool SetAccountStatus(Guid objectGuid, bool enabled, bool skipMetaUpdate)
        {
            var obj = this.FindObject(objectGuid);
            return this.SetAccountStatus(obj, objectGuid, enabled, skipMetaUpdate);
        }

        public bool SetPrimaryGroupId(DistinguishedName dn, int groupId, bool skipMetaUpdate)
        {
            var obj = this.FindObject(dn);
            return this.SetPrimaryGroupId(obj, dn, groupId, skipMetaUpdate);
        }

        public bool SetPrimaryGroupId(string samAccountName, int groupId, bool skipMetaUpdate)
        {
            var obj = this.FindObject(samAccountName);
            return this.SetPrimaryGroupId(obj, samAccountName, groupId, skipMetaUpdate);
        }

        public bool SetPrimaryGroupId(SecurityIdentifier objectSid, int groupId, bool skipMetaUpdate)
        {
            var obj = this.FindObject(objectSid);
            return this.SetPrimaryGroupId(obj, objectSid, groupId, skipMetaUpdate);
        }

        public bool SetPrimaryGroupId(Guid objectGuid, int groupId, bool skipMetaUpdate)
        {
            var obj = this.FindObject(objectGuid);
            return this.SetPrimaryGroupId(obj, objectGuid, groupId, skipMetaUpdate);
        }

        protected bool AddSidHistory(DatastoreObject targetObject, object targetObjectIdentifier, SecurityIdentifier[] sidHistory, bool skipMetaUpdate)
        {
            if (!targetObject.IsSecurityPrincipal)
            {
                throw new DirectoryObjectOperationException(Resources.ObjectNotSecurityPrincipalMessage, targetObjectIdentifier);
            }
            using (var transaction = this.context.BeginTransaction())
            {
                this.dataTableCursor.BeginEditForUpdate();
                bool hasChanged = targetObject.AddAttribute(CommonDirectoryAttributes.SIDHistory, sidHistory);
                this.CommitAttributeUpdate(targetObject, CommonDirectoryAttributes.SIDHistory, transaction, hasChanged, skipMetaUpdate);
                return hasChanged;
            }
        }

        protected void CommitAttributeUpdate(DatastoreObject obj, string attributeName, IsamTransaction transaction, bool hasChanged, bool skipMetaUpdate)
        {
            if (hasChanged)
            {
                if (!skipMetaUpdate)
                {
                    // Increment the current USN
                    long currentUsn = ++this.context.DomainController.HighestCommittedUsn;
                    DateTime now = DateTime.Now;
                    obj.UpdateAttributeMeta(attributeName, currentUsn, now);
                }
                this.dataTableCursor.AcceptChanges();
                transaction.Commit();
            }
            else
            {
                // No changes have been made to the object
                this.dataTableCursor.RejectChanges();
                transaction.Abort();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing)
            {
                return;
            }
            if (this.dataTableCursor != null)
            {
                this.dataTableCursor.Dispose();
                this.dataTableCursor = null;
            }
            if (this.ownsContext && this.context != null)
            {
                this.context.Dispose();
                this.context = null;
            }
        }

        protected bool SetAccountStatus(DatastoreObject targetObject, object targetObjectIdentifier, bool enabled, bool skipMetaUpdate)
        {
            using (var transaction = this.context.BeginTransaction())
            {
                // Read the current value first. We do not want to touch any other flags.
                int? numericUac;
                targetObject.ReadAttribute(CommonDirectoryAttributes.UserAccountControl, out numericUac);

                if(!numericUac.HasValue)
                {
                    // This object does not have the userAccountControl attribute, so it probably is not an account.
                    throw new DirectoryObjectOperationException(Resources.ObjectNotAccountMessage, targetObjectIdentifier);
                }

                var uac = (UserAccountControl)numericUac.Value;
                if(enabled)
                {
                    // Clear the ADS_UF_ACCOUNTDISABLE flag
                    uac &= ~UserAccountControl.Disabled;
                }
                else
                {
                    // Set the ADS_UF_ACCOUNTDISABLE flag
                    uac |= UserAccountControl.Disabled;
                }

                this.dataTableCursor.BeginEditForUpdate();
                bool hasChanged = targetObject.SetAttribute<int>(CommonDirectoryAttributes.UserAccountControl, (int?)uac);
                this.CommitAttributeUpdate(targetObject, CommonDirectoryAttributes.UserAccountControl, transaction, hasChanged, skipMetaUpdate);
                return hasChanged;
            }
        }

        protected bool SetPrimaryGroupId(DatastoreObject targetObject, object targetObjectIdentifier, int groupId, bool skipMetaUpdate)
        {
            if (!targetObject.IsAccount)
            {
                throw new DirectoryObjectOperationException(Resources.ObjectNotAccountMessage, targetObjectIdentifier);
            }
            // TODO: Validator.ValidateRid
            // TODO: Test if the rid exists?
            using (var transaction = this.context.BeginTransaction())
            {
                this.dataTableCursor.BeginEditForUpdate();
                bool hasChanged = targetObject.SetAttribute<int>(CommonDirectoryAttributes.PrimaryGroupId, groupId);
                this.CommitAttributeUpdate(targetObject, CommonDirectoryAttributes.PrimaryGroupId, transaction, hasChanged, skipMetaUpdate);
                return hasChanged;
            }
        }
    }
}