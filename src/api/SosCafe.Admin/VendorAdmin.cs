﻿using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Table;
using SosCafe.Admin.Csv;
using SosCafe.Admin.Entities;
using SosCafe.Admin.Models.Api;
using System.Text.RegularExpressions;
using PhoneNumbers;

namespace SosCafe.Admin
{
    public static class VendorAdmin
    {
        private static readonly PhoneNumberUtil PhoneNumberUtil = PhoneNumbers.PhoneNumberUtil.GetInstance();

        [FunctionName("AdminGetVendor")]
        public static IActionResult AdminGetVendor(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/vendors/{vendorId}")] HttpRequest req,
            ClaimsPrincipal claimsPrincipal,
            string vendorId,
            [Table("Vendors", "Vendors", "{vendorId}", Connection = "SosCafeStorage")] VendorDetailsEntity vendorDetailsEntity,
            ILogger log)
        {
            // Check the authorisation.
            var isAuthorised = UserManagement.IsUserAuthorisedForAdmin(claimsPrincipal);
            if (!isAuthorised)
            {
                log.LogInformation("Received unauthorised admin request. Denying request.");
                return new NotFoundResult();
            }

            // Read the vendor details from table storage.
            if (vendorDetailsEntity == null)
            {
                log.LogWarning("Could not find vendor {VendorId}.", vendorId);
                return new NotFoundResult();
            }

            // Map to an API response.
            var vendorDetailsResponse = new InternalVendorDetailsApiModel
            {
                Id = vendorDetailsEntity.ShopifyId,
                RegisteredDate = vendorDetailsEntity.RegisteredDate,
                BusinessName = vendorDetailsEntity.BusinessName,
                ContactName = vendorDetailsEntity.ContactName,
                EmailAddress = vendorDetailsEntity.EmailAddress,
                PhoneNumber = vendorDetailsEntity.PhoneNumber,
                BankAccountNumber = vendorDetailsEntity.BankAccountNumber,
                HasAgreedToTerms = (vendorDetailsEntity.DateAcceptedTerms != null),
                IsClickAndCollect = vendorDetailsEntity.IsClickAndCollect,
                InternalTag = vendorDetailsEntity.InternalTag
            };

            // Return the vendor details.
            return new OkObjectResult(vendorDetailsResponse);
        }

        [FunctionName("AdminUpdateVendor")]
        public static async Task<IActionResult> AdminUpdateVendor(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/vendors/{vendorId}")] InternalUpdateVendorDetailsApiModel vendorDetailsApiModel,
            HttpRequest req,
            ClaimsPrincipal claimsPrincipal,
            string vendorId,
            [Table("Vendors", "Vendors", "{vendorId}", Connection= "SosCafeStorage")] VendorDetailsEntity vendorDetailsEntity,
            [Table("Vendors", Connection = "SosCafeStorage")] CloudTable vendorDetailsTable,
            ILogger log)
        {
            // Check the authorisation.
            var isAuthorised = UserManagement.IsUserAuthorisedForAdmin(claimsPrincipal);
            if (!isAuthorised)
            {
                log.LogInformation("Received unauthorised admin request. Denying request.");
                return new NotFoundResult();
            }

            // Perform validation on the properties.
            if (!VendorManagement.BankAccountRegex.IsMatch(vendorDetailsApiModel.BankAccountNumber))
            {
                return new BadRequestErrorMessageResult("The bank account number is invalid.");
            }

            // Update entity.
            vendorDetailsEntity.ContactName = vendorDetailsApiModel.ContactName;
            vendorDetailsEntity.PhoneNumber = vendorDetailsApiModel.PhoneNumber;
            vendorDetailsEntity.BankAccountNumber = vendorDetailsApiModel.BankAccountNumber;
            vendorDetailsEntity.IsClickAndCollect = vendorDetailsApiModel.IsClickAndCollect; // TODO is this working?
            vendorDetailsEntity.InternalTag = vendorDetailsApiModel.InternalTag;

            // Submit entity update to table.
            var replaceVendorDetailsEntityOperation = TableOperation.Replace(vendorDetailsEntity);
            var replaceVendorDetailsEntityOperationResult = await vendorDetailsTable.ExecuteAsync(replaceVendorDetailsEntityOperation);
            if (replaceVendorDetailsEntityOperationResult.HttpStatusCode < 200 || replaceVendorDetailsEntityOperationResult.HttpStatusCode > 299)
            {
                log.LogError("Failed to replace entity in Vendors table. Status code={InsertStatusCode}, Result={InsertResult}", replaceVendorDetailsEntityOperationResult.HttpStatusCode, replaceVendorDetailsEntityOperationResult.Result);
                return new InternalServerErrorResult();
            }
            else
            {
                log.LogInformation("Replaced entity in Vendors table.");
                return new OkResult();
            }
        }

        [FunctionName("AdminSearchVendors")]
        public static async Task<IActionResult> AdminSearchVendors(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/vendors")] HttpRequest req,
            ClaimsPrincipal claimsPrincipal,
            [Table("Vendors", Connection = "SosCafeStorage")] CloudTable vendorsTable,
            ILogger log)
        {
            // Check the authorisation.
            var isAuthorised = UserManagement.IsUserAuthorisedForAdmin(claimsPrincipal);
            if (!isAuthorised)
            {
                log.LogInformation("Received unauthorised admin request. Denying request.");
                return new NotFoundResult();
            }

            // Assemble the query filters.
            var filters = new List<string>();

            string vendorId = req.Query["vendorId"];
            if (!string.IsNullOrEmpty(vendorId))
            {
                filters.Add(TableQuery.GenerateFilterCondition(nameof(VendorDetailsEntity.RowKey), QueryComparisons.Equal, vendorId));
            }

            string name = req.Query["name"];
            if (!string.IsNullOrEmpty(name))
            {
                filters.Add(TableQuery.GenerateFilterCondition(nameof(VendorDetailsEntity.BusinessName), QueryComparisons.Equal, name));
            }

            string emailAddress = req.Query["emailAddress"];
            if (!string.IsNullOrEmpty(emailAddress))
            {
                filters.Add(TableQuery.GenerateFilterCondition(nameof(VendorDetailsEntity.EmailAddress), QueryComparisons.Equal, emailAddress));
            }

            string tag = req.Query["tag"];
            if (!string.IsNullOrEmpty(tag))
            {
                filters.Add(TableQuery.GenerateFilterCondition(nameof(VendorDetailsEntity.InternalTag), QueryComparisons.Equal, tag));
            }

            if (!filters.Any())
            {
                return new BadRequestErrorMessageResult("You must specify at least one filter criteria.");
            }

            var filter = CombineTableFilters(filters);

            // Execute the query.
            TableContinuationToken token = null;
            var searchResults = new List<VendorDetailsEntity>();
            do
            {
                var queryResult = await vendorsTable.ExecuteQuerySegmentedAsync(new TableQuery<VendorDetailsEntity>().Where(filter), token);
                searchResults.AddRange(queryResult.Results);
                token = queryResult.ContinuationToken;
            } while (token != null);

            // Map the results to a response model.
            var mappedResults = searchResults.Select(entity => new VendorSummaryApiModel
            {
                Id = entity.ShopifyId,
                BusinessName = entity.BusinessName
            })
                .OrderBy(d => d.BusinessName);

            // Return the results.
            return new OkObjectResult(mappedResults);
        }

        [FunctionName("AdminExportVendorList")]
        public static async Task<IActionResult> AdminExportVendorList(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/vendors/csv")] HttpRequest req,
            ClaimsPrincipal claimsPrincipal,
            [Table("Vendors", Connection = "SosCafeStorage")] CloudTable vendorDetailsTable,
            ILogger log)
        {
            // Check the authorisation.
            var isAuthorised = UserManagement.IsUserAuthorisedForAdmin(claimsPrincipal);
            if (!isAuthorised)
            {
                log.LogInformation("Received unauthorised admin request. Denying request.");
                return new NotFoundResult();
            }

            // Read all records from table storage.
            TableContinuationToken token = null;
            var allVendorDetails = new List<VendorDetailsEntity>();
            do
            {
                var queryResult = await vendorDetailsTable.ExecuteQuerySegmentedAsync(new TableQuery<VendorDetailsEntity>(), token);
                allVendorDetails.AddRange(queryResult.Results);
                token = queryResult.ContinuationToken;
            } while (token != null);

            // Transform into VendorDetailsCsv objects so that we can roundtrip successfully.
            var allVendorDetailsCsv = allVendorDetails.Select(entity => new VendorDetailsCsv
            {
                ShopifyId = entity.ShopifyId,
                BusinessName = entity.BusinessName,
                RegisteredDate = entity.RegisteredDate,
                ContactName = entity.ContactName,
                PhoneNumber = entity.PhoneNumber,
                EmailAddress = entity.EmailAddress,
                BankAccountNumber = entity.BankAccountNumber,
                IsClickAndCollect = entity.IsClickAndCollect,
                InternalTag = entity.InternalTag
            })
                .OrderBy(d => d.BusinessName);

            // Serialize to CSV.
            var fileBytes = CsvCreator.CreateCsvFile(allVendorDetailsCsv);
            return new FileContentResult(fileBytes, "text/csv")
            {
                FileDownloadName = "SOSCafe-AllVendors.csv"
            };
        }

        private static string CombineTableFilters(List<string> filters)
        {
            string myQuery = string.Empty;
            var isFirst = true;

            foreach (string filter in filters)
            {
                if (isFirst)
                {
                    myQuery = filter;
                }
                else
                {
                    myQuery = TableQuery.CombineFilters(myQuery, TableOperators.And, filter);
                }
            }

            return myQuery;
        }

        private static string FormatBankAccountNumber(string bankAccountNumber)
        {
            string bankAccountNumberWithNumbersOnly = string.Concat(bankAccountNumber.Where(char.IsDigit));
            if (bankAccountNumberWithNumbersOnly.Length < 15 || bankAccountNumberWithNumbersOnly.Length > 16) return bankAccountNumberWithNumbersOnly;
            return Regex.Replace(bankAccountNumberWithNumbersOnly, @"(\w{2})(\w{4})(\w{7})(\w{2,3})", @"$1-$2-$3-$4");
        }

        private static string FormatPhoneNumber(string phoneNumber)
        {
            try
            {
                var phoneNumberParsed = PhoneNumberUtil.Parse(phoneNumber.Trim(), "NZ");
                return PhoneNumberUtil.Format(phoneNumberParsed, PhoneNumberFormat.NATIONAL);
            }
            catch (NumberParseException)
            {
                return phoneNumber.Trim();
            }
        }
    }
}
