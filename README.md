# GeoLocationFilter

[![Docker](https://img.shields.io/badge/Docker-Available-blue?style=flat-square&logo=docker)](https://github.com/mklouda4/geolocationfilter)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-purple?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow?style=flat-square)](https://opensource.org/licenses/MIT)
[![Prometheus](https://img.shields.io/badge/Metrics-Prometheus-orange?style=flat-square&logo=prometheus)](https://prometheus.io/)

A high-performance REST API service for intelligent geolocation-based request filtering. Built with .NET 8, it provides country-based access control with comprehensive monitoring and enterprise-grade features.

## 🚀 Key Features

### **Security & Access Control**
- **Country-based filtering** with allowlist/blocklist support
- **Dynamic configuration** via query parameters for real-time policy changes
- **MaxMind GeoLite2** integration for accurate geolocation
- **Fallback API support** for enhanced reliability
- **Local IP detection** with configurable CIDR ranges
- **Rate limiting** with per-endpoint configuration

### **Production Ready**
- **Docker & Portainer** deployment support
- **Prometheus metrics** for comprehensive monitoring
- **Health checks** and status endpoints
- **Swagger/OpenAPI** documentation
- **Configurable caching** for optimal performance

### **Developer Experience**
- Environment variable configuration
- Real-time configuration override via query parameters
- Structured logging
- RESTful API design
- Production and development profiles

---

## 📋 Requirements

- **.NET 8.0** Runtime
- **MaxMind GeoLite2-Country.mmdb** database
- **Docker** (optional, for containerized deployment)

---

## 🏃‍♂️ Quick Start

### Local Development

1. **Download GeoLite2 Database**
   ```bash
   # Place GeoLite2-Country.mmdb in the Data directory
   mkdir Data
   # Download from MaxMind (registration required)
   ```

2. **Configure Application**
   ```json
   {
     "Security": {
       "IgnoreLocalIps": true,
       "BlockUnknown": true,
       "AllowedCountries": ["CZ", "SK"],
       "BlockedCountries": ["CN", "RU"]
     },
     "DbPath": "./Data/GeoLite2-Country.mmdb"
   }
   ```

3. **Run Application**
   ```bash
   dotnet run --project GeoLocationFilter.csproj
   ```
   
   🌐 **Access:** http://localhost:8080  
   📚 **Documentation:** http://localhost:8080/swagger (development mode)

### Docker Deployment

```bash
# Build container
docker build -t geolocationfilter .

# Run with volume mount
docker run -p 8080:8080 \
  -v $(pwd)/Data:/data \
  -e ALLOWED_COUNTRIES=CZ,SK \
  -e BLOCK_UNKNOWN=true \
  geolocationfilter
```

### Portainer Stack

```yaml
version: '3.8'

services:
  geolocationfilter:
    image: ghcr.io/mklouda4/geolocationfilter:latest
    container_name: geolocationfilter
    ports:
      - "${HTTP_PORT:-8091}:8080"
    environment:
      # Application Settings
      - TZ=Europe/Prague
      - ASPNETCORE_ENVIRONMENT=Production
      - ASPNETCORE_URLS=http://+:8080
      - ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
      
      # Security Configuration
      - BLOCK_UNKNOWN=true
      - IGNORE_LOCAL_IPS=true
      - ALLOWED_COUNTRIES=CZ,SK
      - BLOCKED_COUNTRIES=CN,RU
      - LOCAL_IPS=127.0.0.0/8,192.168.0.0/16,10.0.0.0/8,172.16.0.0/12
      
      # Rate Limiting
      - RATELIMIT_PERMIT_LIMIT=100
      - RATELIMIT_WINDOW_MINUTES=1
      - RATELIMIT_VALIDATION_PERMIT_LIMIT=100
      - RATELIMIT_VALIDATION_WINDOW_MINUTES=1
      - RATELIMIT_HEALTH_PERMIT_LIMIT=20
      - RATELIMIT_HEALTH_WINDOW_MINUTES=1
      - RATELIMIT_BURST_PERMIT_LIMIT=200
      - RATELIMIT_BURST_WINDOW_MINUTES=1
    volumes:
      - /shared/data/maxmind:/data
    restart: unless-stopped
```

---

## ⚙️ Configuration

### Environment Variables

| Variable | Description | Default | Example |
|----------|-------------|---------|---------|
| `DB_PATH` | Path to GeoLite2 database (optional, if not mounted data directory) | `/data/GeoLite2-Country.mmdb` | `/data/GeoLite2-Country.mmdb` |
| `BLOCK_UNKNOWN` | Block requests with unknown country | `false` | `true` |
| `IGNORE_LOCAL_IPS` | Ignore local/private IP ranges | `true` | `true` |
| `ALLOWED_COUNTRIES` | Comma-separated allowlist | - | `CZ,SK,DE` |
| `BLOCKED_COUNTRIES` | Comma-separated blocklist | - | `CN,RU,KP` |
| `LOCAL_IPS` | CIDR ranges for local IPs | See defaults | `192.168.0.0/16,10.0.0.0/8` |
| `FALLBACK_API` | Fallback geolocation API | `https://get.geojs.io/v1/ip/country/{0}` | Custom API URL |

### Rate Limiting Configuration

| Variable | Description | Default |
|----------|-------------|---------|
| `RATELIMIT_PERMIT_LIMIT` | General endpoint rate limit | `100` |
| `RATELIMIT_WINDOW_MINUTES` | Rate limit window | `1` |
| `RATELIMIT_VALIDATION_PERMIT_LIMIT` | Validation endpoint limit | `100` |
| `RATELIMIT_HEALTH_PERMIT_LIMIT` | Health endpoint limit | `20` |
| `RATELIMIT_BURST_PERMIT_LIMIT` | Burst limit | `200` |

---

## 🔌 API Reference

### Core Endpoints

#### **Validate Request**
```http
GET /validate
GET /validate?blockedCountries=CN,RU&blockUnknown=true
```
Validates the current request and returns access status. Supports dynamic configuration via query parameters.

**Query Parameters (Optional):**

| Parameter | Type | Description | Example |
|-----------|------|-------------|---------|
| `blockedCountries` | string | Comma-separated list of country codes to block | `CN,RU,KP` |
| `allowedCountries` | string | Comma-separated list of allowed country codes | `CZ,SK,US` |
| `blockUnknown` | boolean | Block requests from unknown countries | `true` |
| `ignoreLocalIps` | boolean | Ignore local/private IP addresses | `false` |
| `localIps` | string | Comma-separated CIDR ranges for local IPs | `192.168.1.0/24,10.0.0.0/8` |

**Query Parameter Behavior:**
- **Configuration Override**: Query parameters completely replace corresponding configuration values
- **Empty Values**: Empty parameter (`?blockedCountries=`) clears the list 
- **Missing Parameters**: Use values from application configuration
- **Priority**: Query parameters > Environment variables > appsettings.json

**Examples:**
```bash
# Block only China and Russia, override config
GET /validate?blockedCountries=CN,RU

# Clear all blocked countries (allow all)
GET /validate?blockedCountries=

# Allow only Czech Republic and Slovakia
GET /validate?allowedCountries=CZ,SK&blockUnknown=true

# Dynamic local IP configuration
GET /validate?localIps=192.168.1.0/24,10.0.0.0/8&ignoreLocalIps=true

# Use configuration values (no query parameters)
GET /validate
```

**Response:**
```json
{
  "message": "Access granted",
  "country": "CZ",
  "ipAddress": "192.168.1.100",
  "timestamp": "2025-08-15T10:30:00Z"
}
```

**Blocked Response:**
```json
{
  "message": "Access denied",
  "country": "CN",
  "reason": "in-blocklist",
  "ipAddress": "1.2.3.4"
}
```

#### **Get Client IP**
```http
GET /ip
```
Returns the client's detected IP address.

**Response:**
```json
{
  "ipAddress": "192.168.1.100"
}
```

#### **Check IP Country**
```http
GET /check?ipAddress=1.2.3.4
```
Returns the country code for a specific IP address.

**Parameters:**
- `ipAddress` (required): IP address to check

**Response:**
```json
{
  "ipAddress": "1.2.3.4",
  "countryCode": "US"
}
```

### Response Headers

All validation responses include custom headers:

| Header | Description | Example |
|--------|-------------|---------|
| `X-GeoFilter-Access` | Access result | `allowed` / `blocked` |
| `X-GeoFilter-Country` | Detected country code | `CZ` |
| `X-GeoFilter-Reason` | Block/allow reason | `geo-allowed`, `in-blocklist`, `local-ip` |

### System Endpoints

#### **Health Check**
```http
GET /health
```
Returns service health status.

#### **Metrics**
```http
GET /metrics
```
Prometheus-compatible metrics endpoint.

**Available Metrics:**
- `geoguard_requests_total` - Total HTTP requests by country and result
- `geoguard_cache_hits_total` - Cache hit/miss statistics
- `geoguard_geo_api_calls_total` - External API call statistics
- `geoguard_request_duration_seconds` - Request processing time

---

## 🎯 Use Cases

### **API Gateway Integration**
```bash
# Check if request should be allowed before forwarding
curl "https://geofilter.example.com/validate" \
  -H "X-Forwarded-For: $CLIENT_IP" \
  -H "X-Forwarded-Host: api.example.com"
```

### **Dynamic Security Policies**
```bash
# Emergency blocking of specific countries
GET /validate?blockedCountries=CN,RU,KP&blockUnknown=true

# Maintenance mode - allow only local networks
GET /validate?allowedCountries=&localIps=192.168.0.0/16&ignoreLocalIps=true

# Testing with specific configuration
GET /validate?blockedCountries=&allowedCountries=CZ&blockUnknown=false
```

### **Load Balancer Health Checks**
```bash
# Configure your load balancer to use:
GET /health
```

---

## 📊 Monitoring & Observability

### Prometheus Integration

The service exposes comprehensive metrics for monitoring:

```prometheus
# Request metrics
geoguard_requests_total{country="CZ",result="allowed"}
geoguard_requests_total{country="RU",result="blocked"}

# Performance metrics
geoguard_cache_hits_total{type="hit"}
geoguard_geo_api_calls_total
```

### Sample Grafana Dashboard Queries

```promql
# Request rate by country
rate(geoguard_requests_total[$__rate_interval])

# Cache hit ratio
rate(geoguard_cache_hits_total[$__rate_interval]) / (rate(geoguard_cache_hits_total[$__rate_interval]) + rate(geoguard_cache_misses_total[$__rate_interval])) * 100

# Response time percentiles
histogram_quantile(0.95, rate(geoguard_request_duration_seconds_bucket[$__rate_interval])) * 1000
```

---

## 🛡️ Security Considerations

- **IP Validation**: Supports IPv4 and IPv6 with proper validation
- **Rate Limiting**: Configurable per-endpoint protection
- **Private IP Handling**: Automatic detection of local/private networks
- **Fallback Security**: Graceful handling of database unavailability
- **Dynamic Configuration**: Query parameters allow real-time policy adjustments
- **Logging**: Comprehensive security event logging with configuration tracking

---

## 🚀 Production Deployment

### Best Practices

1. **Database Updates**: Regularly update MaxMind GeoLite2 database
2. **Monitoring**: Set up alerts for high error rates and cache misses
3. **Rate Limiting**: Configure appropriate limits based on expected traffic
4. **Health Checks**: Use the `/health` endpoint for load balancer probes
5. **Logging**: Configure structured logging for production environments
6. **Query Parameter Security**: Consider implementing authentication for dynamic configuration endpoints in production

### Performance Tuning

- **Database Location**: Place GeoLite2 database on fast storage
- **Rate Limiting**: Fine-tune limits to balance security and performance

---

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add some amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

---

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

---

## 🆘 Support

- **Issues**: [GitHub Issues](https://github.com/mklouda4/geolocationfilter/issues)
- **Documentation**: Available in `/swagger` endpoint (development mode)
- **MaxMind Database**: [Register for free access](https://dev.maxmind.com/geoip/geolite2-free-geolocation-data)

---

<div align="center">

**Built with ❤️ using .NET 8**

</div>