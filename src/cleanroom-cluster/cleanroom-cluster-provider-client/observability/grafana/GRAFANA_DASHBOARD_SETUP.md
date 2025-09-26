# Grafana Dashboard Setup Guide

This guide describes how to create custom Grafana dashboards for monitoring the Cleanroom and export them as JSON.

## Creating a Grafana Dashboard

### Step 1: Access Grafana UI

1. In the K8s Cluster, find the service "cleanroom-spark-grafana" in the namespace "telemetry".
2. Enable port-forwarding of the service.
3. Find the `admin-password` using the command `kubectl get secret --namespace telemetry cleanroom-spark-grafana -o jsonpath="{.data.admin-password}" | base64 --decode ; echo`.

    > NOTE:
    > You will have to provide the downloaded kubeconfig of the cluster for the above command to work.
4. The `admin-user` is `admin`.
5. Navigate to your Grafana instance (e.g., `http://localhost:3000`)
6. Log in with your credentials.
7. If you navigate to Dashboards, you should be able to see some pre-created dashboards. If you want to add a new dashboard, go to New -> New Dashboard.

### Step 2: Editing a dashboard

1. Click **"Add Visualization"** on the dashboard or edit an existing one.
2. Configure your panel in the panel editor.
3. Click on Save Dashboard, that will prop out a json if you're editing a Dashboard. Copy the Json to clipboard and replace it under the `telemetry/grafana/dashboards` directory.
4. If you're creating a new Dashboard, Save the Dashboard. Then navigate to `Export` and select `Export dashboard JSON`. Place the file in the above directory.
