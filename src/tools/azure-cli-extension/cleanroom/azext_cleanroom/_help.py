# coding=utf-8
# pylint: disable=missing-module-docstring

from knack.help_files import helps  # pylint: disable=unused-import

helps["cleanroom"] = """
    type: group
    short-summary: Commands to manage clean rooms.
"""

helps["cleanroom governance"] = """
    type: group
    short-summary: Command to perform governance related operations
"""

helps["cleanroom governance client"] = """
    type: group
    short-summary: Command to perform governance client deployment and related operations.
"""

helps["cleanroom governance client deploy"] = """
    type: command
    short-summary: Command to deploy the governance client to interact with a consortium.
"""

helps["cleanroom governance client remove"] = """
    type: command
    short-summary: Command to remove the deployed governance client instance.
"""

helps["cleanroom governance client show"] = """
    type: command
    short-summary: Command to show the details of a governance client instance.
"""
helps["cleanroom governance client show-deployment"] = """
    type: command
    short-summary: Command to show the container deployment details of a governance client instance.
"""

helps["cleanroom governance service"] = """
    type: group
    short-summary: Command to perform governance service deployment and related operations.
"""

helps["cleanroom governance service deploy"] = """
    type: command
    short-summary: Command to deploy the governance service to host a consortium.
"""

helps["cleanroom datastore"] = """
    type: group
    short-summary: Command to perform datastore related operations.
"""

helps["cleanroom datastore initialize"] = """
    type: command
    short-summary: Command to initialize a datastore.
"""


helps["cleanroom ccf network up"] = """
    type: command
    short-summary: Deploys the simplest possible CACI based CCF network, with a single operator, further members can be added later. The key benefit of this command is it's a single call to get an entire network created with all relevant configuration options, and identities stored in a workspace directory.
"""
