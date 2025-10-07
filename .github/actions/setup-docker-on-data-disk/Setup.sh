#!/bin/bash
set -e

# This is the preprovisioning script used in the 1ES infra to move docker's data-root to a
# separate data disk in order to avoid running into disk space issues. Based on the documentation
# at https://eng.ms/docs/cloud-ai-platform/devdiv/one-engineering-system-1es/1es-docs/1es-hosted-azure-devops-pools/provisioning-script
# 1. The name of this file is case-sensitive and has to be Setup.sh
# 2. This script currently lives in the "cleanroombuildscriptsa" storage account under the
#    "cleanroomscripts" container.
# 3. The 1ES image "ubuntu2204-image" is configured to use this script for setup.
#    Link to the image: https://ms.portal.azure.com/#@microsoft.onmicrosoft.com/resource/subscriptions/3b9ce031-3a87-4e70-a8bb-75ec1d90ad22/resourceGroups/cleanroom-build-infra-rg/providers/Microsoft.CloudTest/images/ubuntu2204-image/overview
#
# In case of any changes to this script, please re-upload to the same container. Then, go to the
# image and under Settings > Provisioning script, update the provisioning script version.
#
# Can these steps be performed directly in the workflow instead?
# Yes. However, this step needs a docker restart which takes a significant amount of time.
# With this approach, the platform handles the restart and jobs can start quickly.

# Create target directory
sudo mkdir -p /mnt/storage/docker
sudo chown root:root /mnt/storage/docker

# Write daemon.json with new data-root
sudo mkdir -p /etc/docker
cat <<EOF | sudo tee /etc/docker/daemon.json
{
  "data-root": "/mnt/storage/docker"
}
EOF
