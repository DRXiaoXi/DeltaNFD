# INSIDE existing HTTPS server {} only. Preserve existing site/update locations.
location = /api/feedback {
    limit_except POST { deny all; }
    limit_req zone=deltanfd_feedback burst=2 nodelay;
    limit_req zone=deltanfd_feedback_total burst=4 nodelay;
    limit_req_status 429;
    limit_conn deltanfd_feedback_conn_ip 1;
    limit_conn deltanfd_feedback_conn_total 4;
    limit_conn_status 429;
    # Absent mount/readiness marker: reject before reading/spooling the body.
    if (!-f /var/lib/nginx/deltanfd_feedback_body/.ready) { return 503; }
    client_max_body_size 4m;
    client_body_buffer_size 64k;
    client_body_temp_path /var/lib/nginx/deltanfd_feedback_body 1 2;
    client_body_timeout 10s;
    proxy_request_buffering on;
    proxy_pass http://127.0.0.1:18792;
    proxy_connect_timeout 5s;
    proxy_read_timeout 35s;
    proxy_send_timeout 35s;
    proxy_set_header Host deltaxiaoxi.xin;
    proxy_set_header Connection "";
    proxy_hide_header Server;
    proxy_intercept_errors off;
    access_log off;
    # Do not let per-request rate-limit messages fill the normal error log.
    error_log /var/log/nginx/deltanfd-feedback-error.log crit;
    limit_req_log_level warn;
    limit_conn_log_level warn;
}

location = /api/feedback/health {
    limit_except GET { deny all; }
    limit_req zone=deltanfd_feedback_health_ip burst=1 nodelay;
    limit_req zone=deltanfd_feedback_health_total burst=2 nodelay;
    limit_req_status 429;
    limit_conn deltanfd_feedback_conn_ip 1;
    limit_conn deltanfd_feedback_conn_total 4;
    limit_conn_status 429;
    client_max_body_size 1k;
    proxy_pass_request_body off;
    proxy_set_header Content-Length "";
    proxy_pass http://127.0.0.1:18792;
    proxy_set_header Host deltaxiaoxi.xin;
    proxy_connect_timeout 5s;
    proxy_read_timeout 5s;
    access_log off;
    error_log /var/log/nginx/deltanfd-feedback-error.log crit;
    limit_req_log_level warn;
    limit_conn_log_level warn;
}

location ^~ /api/feedback/ {
    access_log off;
    return 404;
}
