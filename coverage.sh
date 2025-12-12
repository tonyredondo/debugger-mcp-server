#!/bin/bash
# Code Coverage Script for Debugger MCP Server
# Generates coverage reports in multiple formats

set -e

echo "🧪 Running tests with code coverage..."

# Clean previous results
rm -rf ./TestResults

# Run tests with coverage collection using coverlet.collector
dotnet test \
    --collect:"XPlat Code Coverage" \
    --results-directory ./TestResults \
    --settings ./coverlet.runsettings \
    -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura,opencover,lcov

echo ""
echo "📊 Coverage reports generated in ./TestResults/"
echo ""

# Find the coverage file(s) (collector writes under TestResults/<guid>/)
COVERAGE_FILES=$(find ./TestResults -name "coverage.cobertura.xml" | tr '\n' ';')

if [ -n "$COVERAGE_FILES" ]; then
    echo "Coverage files:"
    find ./TestResults -name "coverage.cobertura.xml" -print
    
    # Check if reportgenerator is installed
    if command -v reportgenerator &> /dev/null; then
        echo ""
        echo "📈 Generating HTML report..."
        reportgenerator \
            -reports:"$COVERAGE_FILES" \
            -targetdir:"./TestResults/coverage-report" \
            -reporttypes:"Html;HtmlSummary;Badges;TextSummary"
        
        echo ""
        echo "✅ HTML report generated at: ./TestResults/coverage-report/index.html"
        echo ""
        
        # Show summary
        if [ -f "./TestResults/coverage-report/Summary.txt" ]; then
            cat "./TestResults/coverage-report/Summary.txt"
        fi
    else
        echo ""
        echo "💡 To generate HTML reports, install ReportGenerator:"
        echo "   dotnet tool install -g dotnet-reportgenerator-globaltool"
        echo ""
        echo "Then run this script again."
    fi
else
    echo "⚠️ Coverage file not found"
fi
