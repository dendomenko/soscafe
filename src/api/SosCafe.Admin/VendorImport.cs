using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using System.IO;
using CsvHelper;
using System.Globalization;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Table;
using System.Threading.Tasks;
using SosCafe.Admin.Csv;
using System;
using SosCafe.Admin.Entities;

namespace SosCafe.Admin.Models
{
    public static class VendorImport
    {
        [FunctionName("VendorListImport")]
        public static void VendorListImport(
            [BlobTrigger("vendorlistimport/{name}", Connection="SosCafeStorage")] Stream myBlob, string name,
            [Queue("importvendor"), StorageAccount("SosCafeStorage")] ICollector<VendorDetailsCsv> outputQueueMessages,
            ILogger log)
        {
            // Read the blob contents (in CSV format), shred into strongly typed model objects,
            // and add to a queue for processing.
            log.LogInformation("Processing file {FileName}, length {FileLength}.", name, myBlob.Length);

            using (var reader = new StreamReader(myBlob))
            using (var csv = new CsvReader(reader, new CultureInfo("en-NZ")))
            {
                csv.Configuration.HeaderValidated = null;
                csv.Configuration.MissingFieldFound = null;

                var records = csv.GetRecords<VendorDetailsCsv>().ToList();
                log.LogInformation("Found {RecordCount} records.", records.Count);

                // Add the record to the queue using the output binding.
                records.ForEach(vdCsv => outputQueueMessages.Add(vdCsv));
           }
        }

        [FunctionName("ProcessImportedVendor")]
        public static async Task ProcessImportedVendor(
            [QueueTrigger("importvendor", Connection="SosCafeStorage")] VendorDetailsCsv vendorToImport,
            [Table("Vendors", Connection = "SosCafeStorage")] CloudTable vendorDetailsTable,
            [Table("VendorUserAssignments", Connection = "SosCafeStorage")] CloudTable vendorUserAssignmentsTable,
            ILogger log)
        {
            log.LogInformation("Processing vendor ID {VendorShopifyId}.", vendorToImport.ShopifyId);

            // Convert the data to the entity format.
            var vendorEntity = new VendorDetailsEntity
            {
                ShopifyId = vendorToImport.ShopifyId,
                RegisteredDate = vendorToImport.RegisteredDate,
                BusinessName = vendorToImport.BusinessName,
                ContactName = vendorToImport.ContactName,
                EmailAddress = vendorToImport.EmailAddress,
                PhoneNumber = vendorToImport.PhoneNumber,
                BankAccountNumber = vendorToImport.BankAccountNumber,
                IsValidated = false, // TODO remove this
                DateAcceptedTerms = null // TODO roundtrip this
            };

            // Upsert vendor table entity.
            var upsertVendorDetailsEntityOperation = TableOperation.InsertOrReplace(vendorEntity);
            var upsertVendorDetailsEntityOperationResult = await vendorDetailsTable.ExecuteAsync(upsertVendorDetailsEntityOperation);
            if (upsertVendorDetailsEntityOperationResult.HttpStatusCode < 200 || upsertVendorDetailsEntityOperationResult.HttpStatusCode > 299)
            {
                log.LogError("Failed to upsert entity into Vendors table. Status code={UpsertStatusCode}, Result={InsertResult}", upsertVendorDetailsEntityOperationResult.HttpStatusCode, upsertVendorDetailsEntityOperationResult.Result);
            }
            else
            {
                log.LogInformation("Upserted entity into Vendors table.");
            }

            // Create vendor role assignment for this user.
            var vendorUserAssignmentEntity = new VendorUserAssignmentEntity
            {
                VendorShopifyId = vendorToImport.ShopifyId,
                VendorName = vendorToImport.BusinessName,
                UserId = vendorToImport.EmailAddress
            };

            // Upsert vendor user assignment entity.
            var upsertVendorUserAssignmentEntityOperation = TableOperation.InsertOrReplace(vendorUserAssignmentEntity);
            var upsertVendorUserAssignmentEntityOperationResult = await vendorUserAssignmentsTable.ExecuteAsync(upsertVendorUserAssignmentEntityOperation);
            if (upsertVendorUserAssignmentEntityOperationResult.HttpStatusCode < 200 || upsertVendorDetailsEntityOperationResult.HttpStatusCode > 299)
            {
                log.LogError("Failed to upsert entity into VendorUserAssignments table. Status code={UpsertStatusCode}, Result={InsertResult}", upsertVendorDetailsEntityOperationResult.HttpStatusCode, upsertVendorDetailsEntityOperationResult.Result);
            }
            else
            {
                log.LogInformation("Upserted entity into VendorUserAssignments table.");
            }
        }

        [FunctionName("VendorPaymentsImport")]
        public static void VendorPaymentsImport(
            [BlobTrigger("vendorpaymentsimport/{name}", Connection = "SosCafeStorage")] Stream myBlob, string name,
            [Queue("importvendorpayment"), StorageAccount("SosCafeStorage")] ICollector<VendorPaymentCsv> outputQueueMessages,
            ILogger log)
        {
            // Read the blob contents (in CSV format), shred into strongly typed model objects,
            // and add to a queue for processing.
            log.LogInformation("Processing file {FileName}, length {FileLength}.", name, myBlob.Length);

            using (var reader = new StreamReader(myBlob))
            using (var csv = new CsvReader(reader, new CultureInfo("en-NZ")))
            {
                csv.Configuration.HeaderValidated = null;
                csv.Configuration.MissingFieldFound = null;

                var records = csv.GetRecords<VendorPaymentCsv>().ToList();
                log.LogInformation("Found {RecordCount} records.", records.Count);

                // Add the record to the queue using the output binding.
                records.ForEach(vvCsv => outputQueueMessages.Add(vvCsv));
            }
        }

        [FunctionName("ProcessImportedVendorPayment")]
        public static async Task ProcessImportedVendorPayment(
            [QueueTrigger("importvendorpayment", Connection = "SosCafeStorage")] VendorPaymentCsv vendorPaymentToImport,
            [Table("VendorPayments", Connection = "SosCafeStorage")] CloudTable vendorPaymentsTable,
            ILogger log)
        {
            log.LogInformation("Processing vendor payment with payment ID {PaymentId}.", vendorPaymentToImport.PaymentId);

            // Convert the data to the entity format.
            var vendorPaymentEntity = new VendorPaymentEntity
            {
                VendorId = vendorPaymentToImport.VendorId,
                PaymentId = vendorPaymentToImport.PaymentId,
                PaymentDate = vendorPaymentToImport.PaymentDate,
                BankAccountNumber = vendorPaymentToImport.BankAccountNumber,
                GrossPayment = decimal.Parse(vendorPaymentToImport.GrossPayment, NumberStyles.Currency),
                Fees = decimal.Parse(vendorPaymentToImport.Fees, NumberStyles.Currency),
                NetPayment = decimal.Parse(vendorPaymentToImport.NetPayment, NumberStyles.Currency)
            };

            // Upsert vendor payment table entity.
            var upsertVendorPaymentEntityOperation = TableOperation.InsertOrReplace(vendorPaymentEntity);
            var upsertVendorPaymentEntityOperationResult = await vendorPaymentsTable.ExecuteAsync(upsertVendorPaymentEntityOperation);
            if (upsertVendorPaymentEntityOperationResult.HttpStatusCode < 200 || upsertVendorPaymentEntityOperationResult.HttpStatusCode > 299)
            {
                log.LogError("Failed to upsert entity into VendorPayments table. Status code={UpsertStatusCode}, Result={InsertResult}", upsertVendorPaymentEntityOperationResult.HttpStatusCode, upsertVendorPaymentEntityOperationResult.Result);
            }
            else
            {
                log.LogInformation("Upserted entity into VendorPayments table.");
            }
        }

        [FunctionName("VendorVouchersImport")]
        public static void VendorVouchersImport(
            [BlobTrigger("vendorvouchersimport/{name}", Connection = "SosCafeStorage")] Stream myBlob, string name,
            [Queue("importvendorvoucher"), StorageAccount("SosCafeStorage")] ICollector<VendorVoucherCsv> outputQueueMessages,
            ILogger log)
        {
            // Read the blob contents (in CSV format), shred into strongly typed model objects,
            // and add to a queue for processing.
            log.LogInformation("Processing file {FileName}, length {FileLength}.", name, myBlob.Length);

            using (var reader = new StreamReader(myBlob))
            using (var csv = new CsvReader(reader, new CultureInfo("en-NZ")))
            {
                csv.Configuration.HeaderValidated = null;
                csv.Configuration.MissingFieldFound = null;

                var records = csv.GetRecords<VendorVoucherCsv>().ToList();
                log.LogInformation("Found {RecordCount} records.", records.Count);

                // Add the record to the queue using the output binding.
                records.ForEach(vvCsv => outputQueueMessages.Add(vvCsv));
            }
        }

        [FunctionName("ProcessImportedVendorVoucher")]
        public static async Task ProcessImportedVendorVoucher(
            [QueueTrigger("importvendorvoucher", Connection = "SosCafeStorage")] VendorVoucherCsv vendorVoucherToImport,
            [Table("VendorVouchers", Connection = "SosCafeStorage")] CloudTable vendorVouchersTable,
            ILogger log)
        {
            log.LogInformation("Processing vendor voucher with order ID {OrderId}.", vendorVoucherToImport.OrderId);

            // Convert the data to the entity format.
            var vendorVoucherEntity = new VendorVoucherEntity
            {
                VendorId = vendorVoucherToImport.VendorId,
                OrderId = vendorVoucherToImport.OrderId,
                OrderRef = vendorVoucherToImport.OrderRef,
                OrderDate = vendorVoucherToImport.OrderDate,
                CustomerName = vendorVoucherToImport.CustomerName,
                CustomerEmailAddress = vendorVoucherToImport.CustomerEmailAddress,
                CustomerRegion = vendorVoucherToImport.CustomerRegion,
                CustomerAcceptsMarketing = vendorVoucherToImport.CustomerAcceptsMarketing.Contains("TRUE"),
                VoucherDescription = vendorVoucherToImport.VoucherDescription,
                VoucherQuantity = vendorVoucherToImport.VoucherQuantity,
                VoucherIsDonation = vendorVoucherToImport.VoucherIsDonation.Contains("TRUE"),
                VoucherId = vendorVoucherToImport.VoucherId,
                VoucherGross = vendorVoucherToImport.VoucherGross,
                VoucherFees = vendorVoucherToImport.VoucherFees,
                VoucherNet = vendorVoucherToImport.VoucherNet
            };

            // Upsert vendor voucher table entity.
            var upsertVendorVoucherEntityOperation = TableOperation.InsertOrReplace(vendorVoucherEntity);
            var upsertVendorVoucherEntityOperationResult = await vendorVouchersTable.ExecuteAsync(upsertVendorVoucherEntityOperation);
            if (upsertVendorVoucherEntityOperationResult.HttpStatusCode < 200 || upsertVendorVoucherEntityOperationResult.HttpStatusCode > 299)
            {
                log.LogError("Failed to upsert entity into VendorVouchers table. Status code={UpsertStatusCode}, Result={InsertResult}", upsertVendorVoucherEntityOperationResult.HttpStatusCode, upsertVendorVoucherEntityOperationResult.Result);
            }
            else
            {
                log.LogInformation("Upserted entity into VendorVouchers table.");
            }
        }
    }
}
