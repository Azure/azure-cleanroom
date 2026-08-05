#!/bin/sh

# Initialization script responsible for setting up iptable rules to restrict 
# traffic going out of non-root process. 

dump() {
  iptables-legacy-save
}

trap dump EXIT

ALLOW_PROCESS_UID=0
ALLOW_PROCESS_GID=${ALLOW_PROCESS_UID}

# ipv6 table cleanup
ip6tables-legacy -F || true
ip6tables-legacy -X || true


# ipv4 table cleanup
iptables-legacy -t nat -F
iptables-legacy -t nat -X
iptables-legacy -t filter -F
iptables-legacy -t filter -X

if [ "${1:-}" = "clean" ]; then
  echo "Only cleaning, no new rules added"
  exit 0
fi

# block all ipv6 traffic
ip6tables-legacy -A INPUT -j REJECT || true
ip6tables-legacy -A OUTPUT -j REJECT || true
ip6tables-legacy -A FORWARD -j REJECT || true

echo "Variables:"
echo "----------"
echo "ALLOW_PROCESS_UID=${ALLOW_PROCESS_UID}"
echo "ALLOW_PROCESS_GID=${ALLOW_PROCESS_GID}"
echo

set -o errexit
set -o nounset
set -o pipefail
set -x # command echo

# filter rules

# allow the output traffic coming from root processes - transport layer agnostic
iptables-legacy -t filter -A OUTPUT -m owner --uid-owner ${ALLOW_PROCESS_UID} -m owner --gid-owner ${ALLOW_PROCESS_GID} -j ACCEPT

# handle tcp traffic on loopback interface
iptables-legacy -t filter -A OUTPUT -p tcp -d 127.0.0.1/32 -o lo -j ACCEPT

# traffic where loopback is not added
iptables-legacy -t filter -A OUTPUT -p tcp -s 127.0.0.1/32 -d 127.0.0.1/32 -j ACCEPT

# drop all other packets
iptables-legacy -t filter -A OUTPUT -j DROP

# forward rules
iptables-legacy -t filter -A FORWARD -j DROP

echo "options use-vc" | tee -a /etc/resolv.conf

set +x