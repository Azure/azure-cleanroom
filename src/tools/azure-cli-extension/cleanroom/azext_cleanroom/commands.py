# pylint: disable=too-many-statements
# pylint: disable=missing-module-docstring
# pylint: disable=missing-function-docstring


def load_command_table(self, _):
    with self.command_group("cleanroom governance client") as g:
        g.custom_command("deploy", "governance_client_deploy_cmd")
        g.custom_command("remove", "governance_client_remove_cmd")
        g.custom_command("show", "governance_client_show_cmd")
        g.custom_command("show-deployment", "governance_client_show_deployment_cmd")
        g.custom_command("get-upgrades", "governance_client_get_upgrades_cmd")
        g.custom_command("version", "governance_client_version_cmd")
        g.custom_command("get-access-token", "governance_client_get_access_token_cmd")

    with self.command_group("cleanroom governance service") as g:
        g.custom_command("deploy", "governance_service_deploy_cmd")
        g.custom_command("get-upgrades", "governance_service_get_upgrades_cmd")
        g.custom_command(
            "upgrade-constitution", "governance_service_upgrade_constitution_cmd"
        )
        g.custom_command("upgrade-js-app", "governance_service_upgrade_js_app_cmd")
        g.custom_command("version", "governance_service_version_cmd")

    with self.command_group("cleanroom governance service upgrade") as g:
        g.custom_command("status", "governance_service_upgrade_status_cmd")

    with self.command_group("cleanroom governance contract") as g:
        g.custom_command("create", "governance_contract_create_cmd")
        g.custom_command("propose", "governance_contract_propose_cmd")
        g.custom_command("vote", "governance_contract_vote_cmd")
        g.custom_command("show", "governance_contract_show_cmd")

    with self.command_group("cleanroom governance proposal") as g:
        g.custom_command("create", "governance_proposal_create_cmd")
        g.custom_command("vote", "governance_proposal_vote_cmd")
        g.custom_command("withdraw", "governance_proposal_withdraw_cmd")
        g.custom_command("list", "governance_proposal_list_cmd")
        g.custom_command("show", "governance_proposal_show_cmd")
        g.custom_command("show-actions", "governance_proposal_show_actions_cmd")

    with self.command_group("cleanroom governance user-identity") as g:
        g.custom_command("add", "governance_user_identity_add_cmd")
        g.custom_command("remove", "governance_user_identity_remove_cmd")
        g.custom_command("show", "governance_user_identity_show_cmd")

    with self.command_group("cleanroom governance user-identity invitation") as g:
        g.custom_command("create", "governance_user_identity_invitation_create_cmd")
        g.custom_command("show", "governance_user_identity_invitation_show_cmd")
        g.custom_command("accept", "governance_user_identity_invitation_accept_cmd")

    with self.command_group("cleanroom governance ca") as g:
        g.custom_command("propose-enable", "governance_ca_propose_enable_cmd")
        g.custom_command("generate-key", "governance_ca_generate_key_cmd")
        g.custom_command("propose-rotate-key", "governance_ca_propose_rotate_key_cmd")
        g.custom_command("show", "governance_ca_show_cmd")

    with self.command_group("cleanroom governance deployment") as g:
        g.custom_command("generate", "governance_deployment_generate_cmd")

    with self.command_group("cleanroom governance deployment template") as g:
        g.custom_command("propose", "governance_deployment_template_propose_cmd")
        g.custom_command("show", "governance_deployment_template_show_cmd")

    with self.command_group("cleanroom governance deployment policy") as g:
        g.custom_command("propose", "governance_deployment_policy_propose_cmd")
        g.custom_command("show", "governance_deployment_policy_show_cmd")

    with self.command_group("cleanroom governance oidc-issuer") as g:
        g.custom_command("propose-enable", "governance_oidc_issuer_propose_enable_cmd")
        g.custom_command(
            "generate-signing-key", "governance_oidc_issuer_generate_signing_key_cmd"
        )
        g.custom_command(
            "propose-rotate-signing-key",
            "governance_oidc_issuer_propose_rotate_signing_key_cmd",
        )
        g.custom_command("set-issuer-url", "governance_oidc_issuer_set_issuer_url_cmd")
        g.custom_command(
            "propose-set-issuer-url",
            "governance_oidc_issuer_propose_set_issuer_url_cmd",
        )
        g.custom_command("show", "governance_oidc_issuer_show_cmd")

    with self.command_group("cleanroom governance contract runtime-option") as g:
        g.custom_command("get", "governance_contract_runtime_option_get_cmd")
        g.custom_command("set", "governance_contract_runtime_option_set_cmd")
        g.custom_command("propose", "governance_contract_runtime_option_propose_cmd")

    with self.command_group("cleanroom governance contract secret") as g:
        g.custom_command("set", "governance_contract_secret_set_cmd")
        g.custom_command("list", "governance_contract_secret_list_cmd")
        g.custom_command(
            "get-cleanroom-policy",
            "governance_contract_secret_get_cleanroom_policy_cmd",
        )

    with self.command_group("cleanroom governance contract event") as g:
        g.custom_command("list", "governance_contract_event_list_cmd")

    with self.command_group("cleanroom governance member-document") as g:
        g.custom_command("create", "governance_member_document_create_cmd")
        g.custom_command("propose", "governance_member_document_propose_cmd")
        g.custom_command("vote", "governance_member_document_vote_cmd")
        g.custom_command("show", "governance_member_document_show_cmd")

    with self.command_group("cleanroom governance user-document") as g:
        g.custom_command("create", "governance_user_document_create_cmd")
        g.custom_command("propose", "governance_user_document_propose_cmd")
        g.custom_command("vote", "governance_user_document_vote_cmd")
        g.custom_command("show", "governance_user_document_show_cmd")

    with self.command_group("cleanroom governance user-document runtime-option") as g:
        g.custom_command("get", "governance_user_document_runtime_option_get_cmd")
        g.custom_command("set", "governance_user_document_runtime_option_set_cmd")

    with self.command_group("cleanroom governance network") as g:
        g.custom_command("show", "governance_network_show_cmd")
        g.custom_command(
            "set-recovery-threshold", "governance_network_set_recovery_threshold_cmd"
        )

    with self.command_group("cleanroom governance member") as g:
        g.custom_command("keygenerator-sh", "governance_member_keygeneratorsh_cmd")
        g.custom_command(
            "get-default-certificate-policy",
            "governance_member_get_default_certificate_policy_cmd",
        )
        g.custom_command(
            "generate-identity-certificate",
            "governance_member_generate_identity_certificate_cmd",
        )
        g.custom_command(
            "generate-encryption-key", "governance_member_generate_encryption_key_cmd"
        )
        g.custom_command("add", "governance_member_add_cmd")
        g.custom_command("activate", "governance_member_activate_cmd")
        g.custom_command("show", "governance_member_show_cmd")
        g.custom_command("set-tenant-id", "governance_member_set_tenant_id_cmd")

    with self.command_group("cleanroom config") as g:
        g.custom_command("init", "config_init_cmd")
        g.custom_command("view", "config_view_cmd")
        g.custom_command("create-kek", "config_create_kek_policy_cmd")
        g.custom_command("wrap-deks", "config_wrap_deks_cmd")
        g.custom_command("wrap-secret", "config_wrap_secret_cmd")
        g.custom_command("add-application", "config_add_application_cmd")
        g.custom_command("validate", "config_validate_cmd")
        g.custom_command("add-datasource", "config_add_datasource_cmd")
        g.custom_command("add-datasink", "config_add_datasink_cmd")
        g.custom_command("set-telemetry", "config_set_telemetry_cmd")
        g.custom_command("set-logging", "config_set_logging_cmd")

    with self.command_group("cleanroom config network http") as g:
        g.custom_command("enable", "config_network_http_enable_cmd")
        g.custom_command("disable", "config_network_http_disable_cmd")

    with self.command_group("cleanroom config network tcp") as g:
        g.custom_command("enable", "config_network_tcp_enable_cmd")
        g.custom_command("disable", "config_network_tcp_disable_cmd")

    with self.command_group("cleanroom config network dns") as g:
        g.custom_command("enable", "config_network_dns_enable_cmd")
        g.custom_command("disable", "config_network_dns_disable_cmd")

    with self.command_group("cleanroom telemetry") as g:
        g.custom_command("download", "telemetry_download_cmd")
        g.custom_command("decrypt", "telemetry_decrypt_cmd")
        g.custom_command("aspire-dashboard", "telemetry_aspire_dashboard_cmd")

    with self.command_group("cleanroom logs") as g:
        g.custom_command("download", "logs_download_cmd")
        g.custom_command("decrypt", "logs_decrypt_cmd")

    with self.command_group("cleanroom cluster provider") as g:
        g.custom_command("deploy", "cluster_provider_deploy_cmd")
        g.custom_command("remove", "cluster_provider_remove_cmd")

    with self.command_group("cleanroom cluster") as g:
        g.custom_command("create", "cluster_create_cmd")
        g.custom_command("update", "cluster_update_cmd")
        g.custom_command("show", "cluster_show_cmd")
        g.custom_command("get-kubeconfig", "cluster_get_kubeconfig_cmd")
        g.custom_command("delete", "cluster_delete_cmd")
        g.custom_command("up", "cluster_up_cmd")

    with self.command_group("cleanroom cluster analytics-workload deployment") as g:
        g.custom_command(
            "generate", "cluster_analytics_workload_deployment_generate_cmd"
        )

    with self.command_group("cleanroom ccf provider") as g:
        g.custom_command("deploy", "ccf_provider_deploy_cmd")
        g.custom_command("configure", "ccf_provider_configure_cmd")
        g.custom_command("remove", "ccf_provider_remove_cmd")
        g.custom_command("show", "ccf_provider_show_cmd")

    with self.command_group("cleanroom ccf network") as g:
        g.custom_command("up", "ccf_network_up_cmd")
        g.custom_command("create", "ccf_network_create_cmd")
        g.custom_command("delete", "ccf_network_delete_cmd")
        g.custom_command("update", "ccf_network_update_cmd")
        g.custom_command("recover", "ccf_network_recover_cmd")
        g.custom_command(
            "recover-public-network", "ccf_network_recover_public_network_cmd"
        )
        g.custom_command(
            "submit-recovery-share", "ccf_network_submit_recovery_share_cmd"
        )
        g.custom_command("show", "ccf_network_show_cmd")
        g.custom_command("show-health", "ccf_network_show_health_cmd")
        g.custom_command("show-report", "ccf_network_show_report_cmd")
        g.custom_command("trigger-snapshot", "ccf_network_trigger_snapshot_cmd")
        g.custom_command("transition-to-open", "ccf_network_transition_to_open_cmd")
        g.custom_command(
            "set-recovery-threshold", "ccf_network_set_recovery_threshold_cmd"
        )
        g.custom_command(
            "configure-confidential-recovery",
            "ccf_network_configure_confidential_recovery_cmd",
        )

    with self.command_group("cleanroom ccf network join-policy") as g:
        g.custom_command("show", "ccf_network_join_policy_show_cmd")
        g.custom_command(
            "add-snp-host-data", "ccf_network_join_policy_add_snp_host_data_cmd"
        )
        g.custom_command(
            "remove-snp-host-data", "ccf_network_join_policy_remove_snp_host_data_cmd"
        )

    with self.command_group("cleanroom ccf network security-policy") as g:
        g.custom_command("generate", "ccf_network_security_policy_generate_cmd")
        g.custom_command(
            "generate-join-policy",
            "ccf_network_security_policy_generate_join_policy_cmd",
        )
        g.custom_command(
            "generate-join-policy-from-network",
            "ccf_network_security_policy_generate_join_policy_from_network_cmd",
        )
    with self.command_group("cleanroom ccf network recovery-agent") as g:
        g.custom_command("show", "ccf_network_recovery_agent_show_cmd")
        g.custom_command("show-report", "ccf_network_recovery_agent_show_report_cmd")
        g.custom_command(
            "show-network-report", "ccf_network_recovery_agent_show_network_report_cmd"
        )
        g.custom_command(
            "generate-member", "ccf_network_recovery_agent_generate_member_cmd"
        )
        g.custom_command(
            "activate-member", "ccf_network_recovery_agent_activate_member_cmd"
        )
        g.custom_command(
            "submit-recovery-share",
            "ccf_network_recovery_agent_submit_recovery_share_cmd",
        )
        g.custom_command(
            "set-network-join-policy",
            "ccf_network_recovery_agent_set_network_join_policy_cmd",
        )

    with self.command_group("cleanroom ccf recovery-service") as g:
        g.custom_command("create", "ccf_recovery_service_create_cmd")
        g.custom_command("delete", "ccf_recovery_service_delete_cmd")
        g.custom_command("show", "ccf_recovery_service_show_cmd")

    with self.command_group("cleanroom ccf recovery-service security-policy") as g:
        g.custom_command(
            "generate", "ccf_recovery_service_security_policy_generate_cmd"
        )

    with self.command_group("cleanroom ccf recovery-service api") as g:
        g.custom_command("show-report", "ccf_recovery_service_api_show_report_cmd")

    with self.command_group("cleanroom ccf recovery-service api member") as g:
        g.custom_command("show", "ccf_recovery_service_api_member_show_cmd")
        g.custom_command(
            "show-report", "ccf_recovery_service_api_member_show_report_cmd"
        )

    with self.command_group("cleanroom ccf recovery-service api network") as g:
        g.custom_command(
            "show-join-policy", "ccf_recovery_service_api_network_show_join_policy_cmd"
        )

    with self.command_group("cleanroom ccf consortium-manager") as g:
        g.custom_command("create", "ccf_consortium_manager_create_cmd")
        g.custom_command("show", "ccf_consortium_manager_show_cmd")

    with self.command_group("cleanroom datastore") as g:
        g.custom_command("add", "datastore_add_cmd")
        g.custom_command("upload", "datastore_upload_cmd")
        g.custom_command("download", "datastore_download_cmd")
        g.custom_command("encrypt", "datastore_encrypt_cmd")
        g.custom_command("decrypt", "datastore_decrypt_cmd")

    with self.command_group("cleanroom secretstore") as g:
        g.custom_command("add", "secretstore_add_cmd")

    with self.command_group("cleanroom config add-identity") as g:
        g.custom_command("az-federated", "config_add_identity_az_federated_cmd")
        g.custom_command("az-secret", "config_add_identity_az_secret_cmd")
        g.custom_command("oidc-attested", "config_add_identity_oidc_attested_cmd")
