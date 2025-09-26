#!/bin/bash
set -e

# Container name for blobfuse
container_name=$1

# Mount directory for blobfuse
mount_dir=$2

# Temp directory for blobfuse
temp_dir=$3

# Config file for blobfuse
config_file=$4

# Log file path
log_file=$5

# Temp directory for blobfuse backed by storage
temp_dir_plain=$6

# Mount path for blobfuse backed by storage
mount_dir_plain=$7

# Storage account name
account_name=$8

# Client ID for authentication
client_id=$9

# Config file for blobfuse backed by storage
config_file_plain=${10}

# Log file path for plain mount
log_file_path_plain=${11}

# Container for blobfuse logs and perf results
container_name_perf=${12}

# Block size in MB for performance tests
block_size_mb=16

mkdir -p logs
log_file_path_plain=./logs/${log_file_path_plain}
log_file=./logs/${log_file}

# --------------------------------------------------------------------------------------------------
# Generate config files for blobfuse mount

# Set environment variables
export PLAIN_MOUNT=${mount_dir_plain}
export LOG_FILE_PATH=${log_file}
export BLOCK_SIZE_MB=${block_size_mb}
export MEM_SIZE_MB=4096
export DISK_SIZE_MB=16384

# Create config file for encrypted mount
blobfuse2 gen-test-config \
  --config-file=./testdata/config/encryptor.yaml \
  --container-name=${container_name} \
  --temp-path=${temp_dir} \
  --output-file=${config_file}

# print the config file generated 
echo "Config file generated for encrypted mount:"
cat ${config_file}

export ACCOUNT_TYPE="block"
export STO_ACC_NAME=${account_name}
export AZURE_CLIENT_ID=${client_id}
export LOG_FILE_PATH=${log_file_path_plain}
export ACCOUNT_ENDPOINT="https://${account_name}.blob.core.windows.net"

# Create config file for plain mount
blobfuse2 gen-test-config \
  --config-file=./testdata/config/block_cache.yaml \
  --container-name=${container_name} \
  --temp-path=${temp_dir_plain} \
  --output-file=${config_file_plain}

# print the config file generated
echo "Config file generated for plain mount:"
cat ${config_file_plain}

# --------------------------------------------------------------------------------------------------
# Run performance tests
testnames=("write" "read" "create")
set +e
exit_code=0

for testname in "${testnames[@]}"; 
do
    ./fio_bench.sh $testname $mount_dir $mount_dir_plain $temp_dir $temp_dir_plain $config_file $config_file_plain
    exit_code=$?
    if [ $exit_code -ne 0 ]; then
        echo "FIO test failed for $testname"
        break
    fi
done
set -e
# --------------------------------------------------------------------------------------------------

# Upload blobfuse logs and perf result using azcopy
export AZCOPY_AUTO_LOGIN_TYPE=MSI
export AZCOPY_MSI_CLIENT_ID=${client_id}
azcopy cp ./logs "https://${account_name}.blob.core.windows.net/${container_name_perf}/logs" --recursive
azcopy cp ./perf-results "https://${account_name}.blob.core.windows.net/${container_name_perf}/perf-results" --recursive
exit $exit_code
