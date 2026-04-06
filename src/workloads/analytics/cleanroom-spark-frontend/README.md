# Azure Cleanroom Spark Frontend

This project is a web server built using FastAPI that serves as a frontend for submitting and monitoring Spark jobs in an Azure Cleanroom Cluster. It integrates with Kubernetes to create and manage SparkApplication resources through the Spark Operator.

## Project Structure

```
cleanroom-spark-frontend/
├── src/                       # Source code directory
│   ├── builders/              # Spark application builders for different environments
│   ├── clients/               # Client libraries for external services
│   ├── config/                # Configuration-related modules
│   ├── exceptions/            # Custom exception definitions
│   ├── models/                # Data models and schemas
│   ├── utilities/             # Utility functions and helper classes
│   └── main.py                # Main application entry point
├── helm/                      # Helm chart for Kubernetes deployment
│   ├── README.md              # Helm-specific documentation
│   └── cleanroom-spark-frontend/
│       ├── templates/         # Kubernetes resource templates
│       ├── Chart.yaml         # Chart metadata
│       └── values.yaml        # Default values for the chart
├── requirements.txt           # Python dependencies
└── README.md                  # This file
```

## API Endpoints

### POST /submitPiJob

Submit a Pi computation job to the Spark cluster. This is a simple endpoint that doesn't require a request body and is useful for testing the Spark environment.

**Response**: Job submission confirmation with job name and tracking information

### GET /status/analytics/{job_name}

Retrieve the status of a submitted job by its job name.

## Installation

### Prerequisites

- Python 3.9+
- Kubernetes cluster with Spark Operator installed
- Kubernetes configuration file (kubeconfig)

### Local Development Setup

1. Clone the repository:
   ```bash
   git clone <repository-url>
   cd azure-cleanroom/src/workloads/cleanroom-spark-frontend
   ```

2. Create and activate a virtual environment:
   ```bash
   # Create a virtual environment
   python -m venv .venv
   
   # Activate the virtual environment
   # On Linux/macOS
   source .venv/bin/activate
   
   # On PowerShell
   .\.venv\Scripts\Activate.ps1
   ```

3. Install the required dependencies:
   ```bash
   pip install -r requirements.txt
   ```

4. Configure the application:
   - Update `src/config/config.yaml` with appropriate settings for your environment
   - Ensure you have access to a Kubernetes cluster with the Spark Operator installed

## Running the Server Locally for Testing

Before running the server, make sure you have activated your virtual environment:

```bash
# If not already activated
source .venv/bin/activate  # Linux/macOS
# or
.\.venv\Scripts\Activate.ps1  # PowerShell
```

You can run the FastAPI application using Uvicorn directly from the command line:

```bash
python -m src.main --kubeconfig /path/to/your/kubeconfig --config src/config/config.yaml
```

This will start the server at `http://0.0.0.0:8000`.

### API Documentation

Once the server is running, you can access the auto-generated API documentation at:
- Swagger UI: `http://localhost:8000/docs`
- ReDoc: `http://localhost:8000/redoc`

## Kubernetes Deployment with Helm

The application can be deployed to a Kubernetes cluster using Helm. For detailed deployment instructions and configuration options, see the [Helm Chart README](./helm/README.md).

## Configuration

### Application Configuration

The application is configured through the `config.yaml` file, which includes:

- SparkApplication resource settings
- Compute provider configuration
- Analytics job templates for different job types
- Container image references

You can also use the ConfigMap within the Helm chart to provide custom configuration.

### Command-line Arguments

- `--kubeconfig`: Path to the Kubernetes configuration file
- `--config`: Path to the application configuration file