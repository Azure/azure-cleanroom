// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Net;
using System.Text.Json;
using Controllers;

namespace FrontendSvc.Models.CCF;

public class GetDatasetDocument : GetDocument<DatasetDetails>
{
    public static GetDatasetDocument FromDatasetDetails(
        GetDocumentResponse documentResponse)
    {
        if (string.IsNullOrWhiteSpace(documentResponse.Data))
        {
            throw new ApiException(
            HttpStatusCode.BadRequest,
            new ODataError(
                "InvalidDatasetSpecification",
                "Invalid dataset specification."));
        }

        var datasetSpecification = JsonSerializer.Deserialize<DatasetSpecification>(
            documentResponse.Data);

        if (datasetSpecification == null)
        {
            throw new ApiException(
            HttpStatusCode.BadRequest,
            new ODataError(
                "InvalidDatasetSpecification",
                "Invalid dataset specification json format."));
        }

        return new GetDatasetDocument
        {
            Id = documentResponse.Id,
            Version = documentResponse.Version ?? string.Empty,
            Data = DatasetDetails.FromDatasetSpecification(
                datasetSpecification),
            State = documentResponse.State,
            ProposerId = documentResponse.ProposerId ?? string.Empty,
        };
    }
}
