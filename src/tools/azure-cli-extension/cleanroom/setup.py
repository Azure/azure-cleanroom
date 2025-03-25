#!/usr/bin/env python

import os
from codecs import open
from setuptools import setup, find_packages

try:
    from azure_bdist_wheel import cmdclass
except ImportError:
    from distutils import log as logger

    logger.warn("Wheel is not available, disabling bdist_wheel hook")


# Reference : https://github.com/pypa/pip/blob/main/setup.py
def read(rel_path: str) -> str:
    here = os.path.abspath(os.path.dirname(__file__))
    # intentionally *not* adding an encoding option to open, See:
    #   https://github.com/pypa/virtualenv/issues/201#issuecomment-3145690
    with open(os.path.join(here, rel_path)) as fp:
        return fp.read()


def get_version(rel_path: str) -> str:
    if not os.path.isfile(rel_path):
        logger.warn("Version file not found, using default version 1.0.0")
        return "1.0.0"

    for line in read(rel_path).splitlines():
        if line.startswith("VERSION"):
            delim = '"' if '"' in line else "'"
            print(line.split(delim)[1])
            return line.split(delim)[1]
    raise RuntimeError("Unable to find version string.")


# VERSION needs to be updated in azext_cleanroom/version.py
VERSION = get_version("azext_cleanroom/version.py")
# The full list of classifiers is available at
# https://pypi.python.org/pypi?%3Aaction=list_classifiers
CLASSIFIERS = [
    "Development Status :: 4 - Beta",
    "Intended Audience :: Developers",
    "Intended Audience :: System Administrators",
    "Programming Language :: Python",
    "Programming Language :: Python :: 3",
    "Programming Language :: Python :: 3.6",
    "Programming Language :: Python :: 3.7",
    "Programming Language :: Python :: 3.8",
    "License :: OSI Approved :: MIT License",
]

# Add any additional SDK dependencies here and to requirements.txt
DEPENDENCIES = [
    "python-on-whales==0.71.0",
    "pycryptodome==3.19.1",
    "cryptography==43.0.1",
    "docker>=6.1.0",
    "oras==0.1.29",
]

with open("README.rst", "r", encoding="utf-8") as f:
    README = f.read()
with open("HISTORY.rst", "r", encoding="utf-8") as f:
    HISTORY = f.read()

setup(
    name="cleanroom",
    version=VERSION,
    description="Microsoft Azure Command-Line Tools CleanRoom Extension",
    author="Microsoft Corporation",
    author_email="azcleanroomdev@microsoft.com",
    url="https://github.com/Azure/azure-cleanroom",
    long_description=README + "\n\n" + HISTORY,
    license="MIT",
    classifiers=CLASSIFIERS,
    packages=find_packages(exclude=["tests"]),
    install_requires=DEPENDENCIES,
    package_data={
        "azext_cleanroom": [
            "azext_metadata.json",
            "data/docker-compose.yaml",
            "data/aspire-dashboard/docker-compose.yaml",
            "data/aspire-dashboard/otel-collector-config.yaml",
            "data/ccf-provider/docker-compose.yaml",
            "data/keygenerator.sh",
            "data/azstore.yaml",
            "data/application.yaml",
            "data/publisher-config.yaml",
        ],
        "cleanroom_common.azure_cleanroom_core": [
            "templates/*.json",
            "templates/*.rego",
            "binaries/aes_encryptor.so",
        ],
    },
)
