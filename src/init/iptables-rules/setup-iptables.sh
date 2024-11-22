#!/bin/bash
set -e

NO_NETWORK_MODE="no-network-access"
PROXY_MODE="proxy"
DEFAULT_MODE="$NO_NETWORK_MODE"
SUPPORTED_MODES="$NO_NETWORK_MODE|$PROXY_MODE"

mode=$DEFAULT_MODE

function usage()
{
    echo "Usage:"
    echo "  $0 --mode $DEFAULT_MODE"
    echo "Sets up the iptables firewall rules."
    echo ""
    echo "Supported modes are: $SUPPORTED_MODES"
}

while [ "$1" != "" ]; do
    case $1 in
        -h|-\?|--help)
            usage
            exit 0
            ;;
        -m|--mode)
            mode="$2"
            shift
            ;;
        *)
            break
    esac
    shift
done

# Validate parameters
if ! [[ "$mode" =~ ^($SUPPORTED_MODES)$ ]]; then
    echo "$mode mode is not in $SUPPORTED_MODES"
    exit 1
fi

if [ "$mode" == "$NO_NETWORK_MODE" ]; then
    ./setup-iptables-no-network.sh
elif [ "$mode" == "$PROXY_MODE" ]; then
    ./setup-iptables-proxy.sh
else
    echo "$mode mode is not handled by setup-iptables.sh"
    exit 1
fi
